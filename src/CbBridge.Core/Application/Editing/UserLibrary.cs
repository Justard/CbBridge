using System.Text.Json.Serialization;
using CbBridge.Core.Domain;

namespace CbBridge.Core.Application.Editing;

/// <summary>A test the user wrote in the bridge ("My tests").</summary>
public sealed class SavedTest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }

    /// <summary>The full framework JSON (stored as text so extra fields like releases/versions survive).</summary>
    public string PayloadJson { get; set; } = "";

    [JsonIgnore]
    public TestRunPayload? Payload
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PayloadJson)) return null;
            try
            {
                var payload = TestRunPayload.FromJson(PayloadJson);
                payload.IsLocal = true;
                return payload;
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
    }

    public static SavedTest From(TestRunPayload payload) => new()
    {
        Id = payload.ItemId,
        Name = payload.Name,
        SavedAt = DateTime.UtcNow,
        PayloadJson = payload.ToJson()
    };
}

/// <summary>A named framework address, e.g. "Bench A" → http://10.0.0.12:5001.</summary>
public sealed class SavedTestSystem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}
