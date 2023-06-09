using System;

namespace Updater.Models
{
    public class Profile
    {
        public DateTime Created { get; set; }
        public string GameDir { get; set; }
        public string Icon { get; set; }
        public DateTime LastUsed { get; set; }
        public string LastVersionId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }

        public bool IsForge => LastVersionId.Contains("forge");
        public bool IsFabric => LastVersionId.Contains("fabric");

        public string Toolchain => IsForge ? "Forge" : IsFabric ? "Fabric" : string.Empty;

    }
}
