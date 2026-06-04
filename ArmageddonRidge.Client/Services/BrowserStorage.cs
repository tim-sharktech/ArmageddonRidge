using System.Text.Json;
using Microsoft.JSInterop;

namespace ArmageddonRidge.Client.Services;

/// <summary>
/// Typed JSON helper for browser localStorage persistence.
/// </summary>
/// <param name="js">Browser JavaScript runtime used to access localStorage.</param>
public sealed class BrowserStorage(IJSRuntime js)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads and deserializes a value from localStorage.
    /// </summary>
    public async ValueTask<T?> GetAsync<T>(string key)
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", key);
        if (string.IsNullOrWhiteSpace(json)) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    /// <summary>
    /// Serializes and writes a value to localStorage.
    /// </summary>
    public async ValueTask SetAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await js.InvokeVoidAsync("localStorage.setItem", key, json);
    }
}
