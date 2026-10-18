using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

// Runs as the browser user, without elevation. No command execution or system mutation API.
const string Origin = "chrome-extension://figpmfdodbgjjdhlfadcdkigimbbelll/";
if (args.Length < 1 || args[0] != Origin) return 2;
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
try
{
    var header = new byte[4];
    await input.ReadExactlyAsync(header);
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length is < 2 or > 16384) return 3;
    var request = new byte[length]; await input.ReadExactlyAsync(request);
    using var document = JsonDocument.Parse(request, new() { MaxDepth = 12 });
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("command", out var command)
        || command.ValueKind != JsonValueKind.String || command.GetString() is not ("social.bind" or "social.observe")) return 3;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await using var pipe = new NamedPipeClientStream(".", "DpiBypass.SocialObserver.v2", PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(timeout.Token);
    await pipe.WriteAsync(header, timeout.Token); await pipe.WriteAsync(request, timeout.Token); await pipe.FlushAsync(timeout.Token);
    await pipe.ReadExactlyAsync(header, timeout.Token);
    var responseLength = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (responseLength is < 2 or > 16384) return 3;
    var response = new byte[responseLength]; await pipe.ReadExactlyAsync(response, timeout.Token);
    await output.WriteAsync(header, timeout.Token); await output.WriteAsync(response, timeout.Token); await output.FlushAsync(timeout.Token);
    return 0;
}
catch
{
    var response = "{\"ok\":false,\"reason\":\"HOST_UNAVAILABLE\"}"u8.ToArray();
    var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
    await output.WriteAsync(header); await output.WriteAsync(response); await output.FlushAsync();
    return 1;
}
