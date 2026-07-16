using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuackSnap.Protocol;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, Options) ?? throw new InvalidDataException($"Empty {typeof(T).Name} payload");
}

public sealed record HelloMessage(int V, string DeviceId, string DeviceName);

public sealed record OfferMessage(string FileId, string Name, string Mime, long Size, long CreatedAtUnixMs);

/// <summary>Offset the receiver wants the sender to start from. Equal to Size means "already have it".</summary>
public sealed record AcceptMessage(string FileId, long Offset);

public sealed record DoneMessage(string FileId);

public sealed record AckMessage(string FileId, bool Ok, string? Error = null);

public sealed record ErrorMessage(string Message);

/// <summary>CertDer (base64) carries the full certificate so the peer can seal
/// relay envelopes to it; it must hash to CertFp, which is covered by the MAC.</summary>
public sealed record PairRequest(int V, string DeviceId, string Name, int ListenPort, string CertFp, string Mac, string? CertDer = null);

public sealed record PairResponse(bool Ok, string? DeviceId, string? Name, string? CertFp, string? Mac, string? Error = null,
    string? CertDer = null, string? RelayUrl = null);
