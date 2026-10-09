using System.Text.Json.Serialization;

namespace QwenTray.Updates;

[JsonSerializable(typeof(GitHubRelease))]
internal partial class UpdateSerializerContext : JsonSerializerContext;
