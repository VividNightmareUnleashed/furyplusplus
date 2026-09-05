using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FuryPlusPlus {
    internal sealed class UpdateRelease {
        internal const string PackageName = "com.furyplusplus.addon";
        internal const string RepositoryUrl = "https://github.com/VividNightmareUnleashed/furyplusplus";
        internal const string ListingUrl = "https://vividnightmareunleashed.github.io/furyplusplus/index.json";

        internal string Version { get; }
        internal string ZipUrl => RepositoryUrl + "/releases/download/v" + Version + "/" + PackageName + "-" + Version + ".zip";
        internal string NotesUrl => RepositoryUrl + "/releases/tag/v" + Version;
        internal string Sha256 { get; }
        internal JObject Manifest { get; }

        private UpdateRelease(string version, string sha256, JObject manifest) {
            Version = version;
            Sha256 = sha256;
            Manifest = manifest;
        }

        internal static JObject ReadObject(string json) {
            using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 32, DateParseHandling = DateParseHandling.None }) {
                var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("Unexpected content after JSON document.");
                return result;
            }
        }

        internal static UpdateRelease FindNewer(string json, string installedVersion) {
            if (!ReleaseVersion.IsStable(installedVersion))
                throw new InvalidDataException("The installed Fury++ version is not a stable release.");
            var versions = ReadObject(json)["packages"]?[PackageName]?["versions"] as JObject;
            if (versions == null) throw new InvalidDataException("The listing has no Fury++ releases.");
            UpdateRelease newest = null;
            var best = System.Version.Parse(installedVersion);
            foreach (var entry in versions.Properties()) {
                if (!ReleaseVersion.IsStable(entry.Name)) continue;
                var version = System.Version.Parse(entry.Name);
                if (version <= best) continue;
                var manifest = entry.Value as JObject;
                if (manifest == null || (string)manifest["name"] != PackageName || (string)manifest["version"] != entry.Name)
                    throw new InvalidDataException("Release identity does not match the listing.");
                var sha = (string)manifest["zipSHA256"];
                if (sha == null || sha.Length != 64 || !IsHex(sha))
                    throw new InvalidDataException("Release is missing a valid SHA-256 checksum.");
                var release = new UpdateRelease(entry.Name, sha, manifest);
                if ((string)manifest["url"] != release.ZipUrl)
                    throw new InvalidDataException("Release download is outside the expected GitHub asset path.");
                newest = release;
                best = version;
            }
            return newest;
        }

        private static bool IsHex(string value) {
            foreach (var c in value) {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }
    }
}
