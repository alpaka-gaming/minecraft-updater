using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Flurl.Http;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Updater.Models;
using Updater.Resources;

namespace Updater
{
	internal class Program
	{
		public static IConfiguration Configuration { get; private set; }

		public static ServiceCollection Services { get; private set; }
		public static ServiceProvider Container { get; private set; }

		#region AppSettings

		private static Uri Server => new Uri(Configuration["AppSettings:Server"]);
		private static string GamePath => Path.Combine(OperatingSystem.IsWindows() ? Environment.ExpandEnvironmentVariables("%appdata%") : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");
		private static string Profile => Configuration["AppSettings:Profile"];

		private static Version Version => Assembly.GetExecutingAssembly().GetName().Version;
		private static string Name => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyProductAttribute>()?.Product;

		#endregion

		#region Versions

		private static Dictionary<string, Version> Versions { get; set; }
		public static string LocalPath { get; set; }
		public static Dictionary<string, Profile> Profiles { get; set; }

		#endregion

		#region MOTD

		private static IEnumerable<string> MotdLines { get; set; }

		#endregion


		private static void Main()
		{
			AppDomain.CurrentDomain.UnhandledException += UnhandledException;

			Console.WriteLine($@"{Name} v{Version.ToString(3)}");
			Console.WriteLine("");

			Configuration = new ConfigurationBuilder()
				.AddJsonFile("appsettings.json", false, true)
#if DEBUG
				.AddJsonFile("appsettings.Development.json", true, true)
#endif
				.Build();

			// Initialize Logger
			Log.Logger = new LoggerConfiguration()
				.ReadFrom.Configuration(Configuration)
				.CreateLogger();

			Services = new ServiceCollection();
			Services.AddSingleton(Configuration);
			Container = Services.BuildServiceProvider();

			// Execution
			try
			{
				var cancellationToken = new CancellationTokenSource().Token;
				MainAsync(cancellationToken).GetAwaiter().GetResult();
			}
			catch (Exception e)
			{
				UnhandledException(e, new UnhandledExceptionEventArgs(e, true));
			}

			Console.WriteLine("");
			Console.WriteLine(Strings.PressAnyKey);
			Console.ReadKey();
		}

		public static async Task MainAsync(CancellationToken cancellationToken = default)
		{
			if (!await ConnectServer(cancellationToken)) throw new OperationCanceledException(Strings.UnableToConnect);
			if (!await GetProfile(cancellationToken)) throw new OperationCanceledException(Strings.UnableToGetProfile);
			if (!FindGame()) throw new OperationCanceledException(Strings.UnableToGetGamePath);

			ValidateVersion();
			PrintInfo();

			if (!await ValidateProfile()) throw new OperationCanceledException(Strings.UnableToValidateProfile);

			var profiles = Profiles
				.Where(m => m.Value.Name.Equals(Profile, StringComparison.InvariantCultureIgnoreCase))
				.OrderByDescending(m => m.Value.Created)
				.ToArray();

			if (profiles != null && !profiles.Any()) throw new OperationCanceledException(string.Format(Strings.NoProfileFound, Profile));

			foreach (var profile in profiles)
			{
				var loaderName = string.Empty;
				Version versionFabric;
				Version versionForge;

				Versions.TryGetValue("Fabric", out versionFabric);
				Versions.TryGetValue("Forge", out versionForge);

				if (versionFabric != null) loaderName = $"fabric-loader-{versionFabric}-{Versions["Minecraft"]}".ToLowerInvariant();
				if (versionForge != null) loaderName = $"{Versions["Minecraft"]}-forge-{versionForge}".ToLowerInvariant();

				if (profile.Value.LastVersionId != loaderName)
				{
					Console.ForegroundColor = ConsoleColor.DarkRed;
					Console.WriteLine(Strings.ProfileMismatch, Profile, profile.Value.Name, loaderName, profile.Value.LastVersionId);

					var downloadUrl = string.Empty;
					if (versionFabric != null) downloadUrl = "https://maven.fabricmc.net/net/fabricmc/fabric-installer/0.11.2/fabric-installer-0.11.2.jar";
					if (versionForge != null) downloadUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{Versions["Minecraft"]}-{Versions["Forge"]}/forge-{Versions["Minecraft"]}-{Versions["Forge"]}-installer.jar";
					if (!string.IsNullOrWhiteSpace(downloadUrl)) Console.WriteLine(Strings.DownloadLoaderFrom, downloadUrl);
					Console.ResetColor();
					break;
				}

				var versionPath = $"{Versions["Minecraft"]}-{profile.Value.Toolchain}-{Versions[profile.Value.Toolchain]}".ToLowerInvariant();

				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine($@"{Strings.Profile}: {profile.Value.Name} ({profile.Value.LastVersionId})");
				Console.ResetColor();

				var gamePath = profile.Value.GameDir;
				if (string.IsNullOrWhiteSpace(gamePath)) gamePath = GamePath;
				gamePath = Environment.ExpandEnvironmentVariables(gamePath);

				// Servers
				var serverDatFile = new FileInfo(Path.Combine(gamePath, "servers.dat"));
				var replaceServerDatFile = false;
				if (serverDatFile.Exists)
				{
					Console.WriteLine();
					Console.Write($@"{Strings.ReplaceServers}: ");
					var key = ConsoleKey.Clear;
					var validKeys = CultureInfo.CurrentCulture.TwoLetterISOLanguageName switch
					{
						"en" => new[]
						{
							ConsoleKey.Y, ConsoleKey.N, ConsoleKey.Escape, ConsoleKey.Enter
						},
						"es" => new[]
						{
							ConsoleKey.S, ConsoleKey.N, ConsoleKey.Escape, ConsoleKey.Enter
						},
						_ => Array.Empty<ConsoleKey>()
					};

					while (!validKeys.Contains(key))
						key = Console.ReadKey().Key;

					replaceServerDatFile = key == ConsoleKey.Y || key == ConsoleKey.Enter;
					Console.WriteLine();
				}
				if (replaceServerDatFile || !serverDatFile.Exists)
				{
					try
					{
						var urlTemp = $"{Server}minecraft/downloads/{Profile}/{versionPath}/servers.dat";
						await urlTemp.DownloadFileAsync(serverDatFile.Directory.FullName, cancellationToken: cancellationToken);
					}
					catch (Exception)
					{
						// ignored
					}
				}

				// Options
				var optionsTxtFile = new FileInfo(Path.Combine(gamePath, "options.txt"));
				var replaceOptionsTxtFile = false;
				if (optionsTxtFile.Exists)
				{
					Console.WriteLine();
					Console.Write($@"{Strings.RecommendedOptions}: ");
					var key = ConsoleKey.Clear;
					var validKeys = CultureInfo.CurrentCulture.TwoLetterISOLanguageName switch
					{
						"en" => new[]
						{
							ConsoleKey.Y, ConsoleKey.N, ConsoleKey.Escape, ConsoleKey.Enter
						},
						"es" => new[]
						{
							ConsoleKey.S, ConsoleKey.N, ConsoleKey.Escape, ConsoleKey.Enter
						},
						_ => Array.Empty<ConsoleKey>()
					};

					while (!validKeys.Contains(key))
						key = Console.ReadKey().Key;

					replaceOptionsTxtFile = key == ConsoleKey.Y || key == ConsoleKey.Enter;
					Console.WriteLine();
				}
				if (replaceOptionsTxtFile)
				{
					try
					{
						var tempFile3 = new FileInfo(Path.GetTempFileName());
						var tempPath3 = tempFile3.Directory?.FullName;

						var urlTemp = $"{Server}minecraft/downloads/{Profile}/{versionPath}/options.txt";
						await urlTemp.DownloadFileAsync(tempPath3, tempFile3.Name, cancellationToken: cancellationToken);

						await PatchOptions(tempFile3, optionsTxtFile, cancellationToken);
					}
					catch (Exception)
					{
						// ignored
					}

				}

				// Folders
				Console.WriteLine();
				var folders = new[]
				{
					"mods", "resourcepacks", "shaderpacks"
				};
				foreach (var folder in folders)
				{
					Console.ForegroundColor = ConsoleColor.Cyan;
					Console.WriteLine($@"=> {Strings.Processing} {folder}...");
					Console.ResetColor();

					var modFiles = await GetFiles(folder, versionPath);
					modFiles = modFiles.Where(m => !m.StartsWith("..")).ToList();
					foreach (var item in modFiles)
					{
						var urlTemp = $"{Server}minecraft/downloads/{Profile}/{versionPath}/{folder}/{item}";
						var localFolderPath = Path.Combine(gamePath, folder, item);
						var jarFile = Path.ChangeExtension(localFolderPath, ".jar");
						var zipFile = Path.ChangeExtension(localFolderPath, ".zip");
						var ext = Path.GetExtension(localFolderPath);
						var name = Path.GetFileNameWithoutExtension(localFolderPath);
						try
						{
							if (File.Exists(localFolderPath))
							{
								var localLength = new FileInfo(localFolderPath).Length;
								var remoteLength = await urlTemp.GetLengthAsync();

								if (localLength != remoteLength)
									File.Delete(localFolderPath);
							}

							if (ext == ".jar" || ext == ".bak" || ext == ".zip")
							{
								if (!File.Exists(localFolderPath) && !File.Exists(jarFile) && !File.Exists(zipFile))
								{
									Console.Write($@"    {Strings.Installing}: {name} ");
									var directoryInfo = new FileInfo(localFolderPath).Directory;
									if (directoryInfo != null)
									{
										var localFolder = directoryInfo.FullName;
										await urlTemp.DownloadFileAsync(localFolder, cancellationToken: cancellationToken);

										Console.ForegroundColor = ConsoleColor.Green;
										Console.Write($@"[{Strings.Done}]");
										Console.WriteLine();
										Console.ResetColor();
									}
								}
							}
							else if (ext == ".rem")
							{
								if (File.Exists(jarFile) || File.Exists(zipFile))
								{
									Console.Write($@"    {Strings.Deleting}: {name} ");
									if (File.Exists(jarFile)) File.Delete(jarFile);
									if (File.Exists(zipFile)) File.Delete(zipFile);
									Console.ForegroundColor = ConsoleColor.Green;
									Console.Write($@"[{Strings.Done}]");
									Console.WriteLine();
									Console.ResetColor();
								}
							}
						}
						catch (Exception e)
						{
							Console.ForegroundColor = ConsoleColor.Red;
							Console.Write($@"[{Strings.Error}]");
							Console.WriteLine();
							Console.ResetColor();
							Log.Logger.Error(e, e.Message);
						}
					}
				}

				Console.WriteLine("");
			}
		}
		private static async Task PatchOptions(FileInfo source, FileInfo target, CancellationToken cancellationToken = default)
		{
			var keys = new Dictionary<string, string>();
			var linesSource = await File.ReadAllLinesAsync(source.FullName, cancellationToken);
			foreach (var line in linesSource)
				if (line.StartsWith("resourcePacks"))
					keys.Add("resourcePacks", line.Substring(14));
				else if (line.StartsWith("lang"))
					keys.Add("lang", line.Substring(5));
				else
					keys.Add(line.Split(':')[0], line.Split(':')[1]);

			var linesTarget = await File.ReadAllLinesAsync(target.FullName, cancellationToken);
			for (var index = 0; index < linesTarget.Length; index++)
			{
				var line = linesTarget[index];
				var l = line.Split(':')[0];
				//var r = line.Split(':')[1];
				if (line.StartsWith("resourcePacks") && keys.ContainsKey("resourcePacks"))
				{
					var sourceLineContent = JsonConvert.DeserializeObject<List<string>>(line.Substring(14));
					var targetLineContent = JsonConvert.DeserializeObject<List<string>>(keys["resourcePacks"]).Except(sourceLineContent).ToArray();
					if (targetLineContent.Any())
						linesTarget[index] = $"resourcePacks:[{EncodeNonAsciiCharacters(string.Join(",", sourceLineContent.Concat(targetLineContent).Select(m => $"\"{m}\"").ToArray()))}]";
				}
				else if (line.StartsWith("lang") && keys.ContainsKey("lang"))
					linesTarget[index] = $"lang:{keys["lang"]}";
				else if (line.StartsWith(l) && keys.ContainsKey(l))
					linesTarget[index] = $"{l}:{keys[l]}";
			}

			await File.WriteAllLinesAsync(target.FullName, linesTarget, cancellationToken);
		}

		private static string EncodeNonAsciiCharacters(string value)
		{
			var sb = new StringBuilder();
			foreach (var c in value)
			{
				if (c > 127 || c == 38)
				{
					// This character is too big for ASCII
					var encodedValue = "\\u" + ((int)c).ToString("x4");
					sb.Append(encodedValue);
				}
				else
				{
					sb.Append(c);
				}
			}
			return sb.ToString();
		}
		private static bool PingServer()
		{
			try
			{
				var ping = new Ping();
				var result = ping.Send(Server.Host);
				return (result != null && result.Status == IPStatus.Success);
			}
			catch (PlatformNotSupportedException)
			{
				return true;
			}
			catch (Exception e)
			{
				Log.Logger.Error(e, e.Message);
				return false;
			}
		}

		private static async Task<bool> ConnectServer(CancellationToken cancellationToken = default)
		{
			var attempts = 1;
			var isServerOn = false;
			while (!isServerOn)
			{
				if (attempts >= 5) break;
				Console.Write($@"{Strings.Connecting}:");
				isServerOn = PingServer();
				if (isServerOn)
				{
					Console.ForegroundColor = ConsoleColor.Green;
					Console.Write($@" [{Strings.Done}]");
					Console.WriteLine();
					Console.ResetColor();
				}
				else
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.Write($@" [{Strings.Failed}]");
					Console.WriteLine();
					Console.ResetColor();
					Console.WriteLine(Strings.RetryInSeconds, attempts);
					await Task.Delay(attempts * 1000, cancellationToken);
				}

				attempts++;
			}

			return isServerOn;
		}

		private static void ValidateVersion()
		{
			if (Versions.TryGetValue("Updater", out var version))
			{
				if (Version < version)
				{
					var updaterUrl = "https://github.com/alpaka-gaming/minecraft-updater/releases";
					throw new InvalidOperationException($"{Strings.MustDownloadIn}{Environment.NewLine}{updaterUrl}");
				}
			}
		}

		private static async Task<bool> GetProfile(CancellationToken cancellationToken = default)
		{
			Console.Write(Strings.GetingProfile);
			try
			{
				var tempFile1 = new FileInfo(Path.GetTempFileName());
				var tempPath1 = tempFile1.Directory?.FullName;
				await $"{Server}minecraft/profiles/{Profile}/versions.json".DownloadFileAsync(tempPath1, tempFile1.Name, cancellationToken: cancellationToken);
				var data = await File.ReadAllTextAsync(tempFile1.FullName);
				var definition = JsonConvert.DeserializeObject<JObject>(data);

				Versions = new Dictionary<string, Version>();
				foreach (var item in definition.Children())
					Versions.Add(item.Path, new Version(item.First().ToString() ?? throw new NullReferenceException()));

				var tempFile2 = new FileInfo(Path.GetTempFileName());
				var tempPath2 = tempFile2.Directory?.FullName;
				await $"{Server}minecraft/profiles/{Profile}/motd.txt".DownloadFileAsync(tempPath2, tempFile2.Name, cancellationToken: cancellationToken);

				MotdLines = await File.ReadAllLinesAsync(tempFile2.FullName);

				Console.ForegroundColor = ConsoleColor.Green;
				Console.Write($@" [{Strings.Done}]");
				Console.WriteLine();
				Console.ResetColor();

				return true;
			}
			catch (Exception e)
			{
				Log.Logger.Error(e, e.Message);

				Console.ForegroundColor = ConsoleColor.Red;
				Console.Write($@" [{Strings.Error}]");
				Console.WriteLine();
				Console.ResetColor();

				return false;
			}
		}

		private static bool FindGame()
		{

			Console.Write(Strings.GettingGamePath);

			var installedPath = Environment.ExpandEnvironmentVariables(GamePath);

			var isPathValid = Directory.Exists(installedPath);
			if (isPathValid)
			{
				Console.ForegroundColor = ConsoleColor.Green;
				Console.Write($@" [{Strings.Done}]");
				Console.WriteLine();
				Console.ResetColor();
				return true;
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.Write($@" [{Strings.Failed}]");
				Console.WriteLine();
				Console.ResetColor();
				return false;
			}
		}

		private static async Task<bool> ValidateProfile()
		{
			var profileFiles = new[]
			{
				"launcher_profiles.json", "launcher_profiles_microsoft_store.json", "TlauncherProfiles.json"
			};
			foreach (var item in profileFiles)
			{
				var profileFile = Path.Combine(GamePath, item);
				if (File.Exists(profileFile))
				{
					if (item != "TlauncherProfiles.json")
					{
						var content = await File.ReadAllTextAsync(profileFile);
						var profiles = JsonConvert.DeserializeObject<JObject>(content).Children().Where(m => m.Path == "profiles").ToList();
						var value = profiles.First().First().ToString();
						Profiles = JsonConvert.DeserializeObject<Dictionary<string, Profile>>(value);
					}
					else
					{
						var folders = Directory.GetDirectories(Path.Combine(GamePath, "versions"))
							.Where(m => File.Exists(Path.Combine(m, "TLauncherAdditional.json")));

						Profiles = new Dictionary<string, Profile>();
						foreach (var folder in folders)
						{
							var tlProfileFile = Path.Combine(folder, "TLauncherAdditional.json");
							var content = await File.ReadAllTextAsync(tlProfileFile);
							var tlAdditional = JsonConvert.DeserializeObject<JObject>(content);
							var name = tlAdditional.GetValue("modpack")["name"].Value<string>();
							var jar = tlAdditional.GetValue("jar").Value<string>();
							var versionType = tlAdditional.GetValue("modpack")["version"]["minecraftVersionTypes"][0]["name"].Value<string>();
							var versionName = tlAdditional.GetValue("modpack")["version"]["minecraftVersionName"]["name"].Value<string>();
							var versionId = string.Empty;
							if (versionType.Contains("fabric")) versionId = $"fabric-loader-{versionName}-{jar}".ToLowerInvariant();
							if (versionType.Contains("forge")) versionId = $"{jar}-forge-{versionName}".ToLowerInvariant();

							var profile = new Profile()
							{
								Created = DateTime.Now,
								GameDir = Path.Combine(GamePath, "versions", folder),
								LastUsed = DateTime.Now,
								LastVersionId = versionId,
								Name = name,
								Type = "custom"
							};
							Profiles.Add(profile.Name, profile);
						}
					}
					return Profiles.Any();
				}
			}

			return false;
		}

		private static void PrintInfo()
		{
			Console.WriteLine();
			if (MotdLines != null)
				foreach (var item in MotdLines)
					Console.WriteLine(item);
			Console.WriteLine();
		}

		private static async Task<List<string>> GetFiles(string folder, string versionId)
		{
			var fixString = new Func<string, string>(m => { return HttpUtility.UrlDecode(m.Replace("+", "%2b")); });

			var url = $"{Server}minecraft/downloads/{Profile}/{versionId}/{folder}";

			var files = new List<string>();
			using (var client = new HttpClient())
			{
				var response = await client.GetStreamAsync(url);
				using (var reader = new StreamReader(response))
				{
					var html = reader.ReadToEnd();
					var doc = new HtmlDocument();
					doc.LoadHtml(html);
					var nodes = doc.DocumentNode.SelectNodes("html/body/pre/a");
					var links = nodes.Select(m => m.Attributes.First(a => a.Name == "href").Value);
					files.AddRange(links.Where(m => !m.StartsWith("?") && !m.StartsWith("/")).Select(m => fixString(m)));
				}
			}

			await Task.Yield();
			return files;
		}

		/* ***** */

		private static void UnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			var ex = (Exception)e.ExceptionObject;
			Log.Logger.Error(ex, ex.Message);
			Console.WriteLine();
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine(ex.Message);
			Console.ResetColor();
		}
	}
}
