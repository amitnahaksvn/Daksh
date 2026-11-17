using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Google;

/// <summary>Google Sheets API v4 client. Supply a user OAuth access token per call.</summary>
public sealed class GoogleSheetsIntegration(HttpClient http)
{
    private const string Base = "https://sheets.googleapis.com/v4/spreadsheets/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GoogleSheetsConnection> ProbeAsync(string token, string spreadsheetId, CancellationToken ct = default)
    {
        var spreadsheet = await GetSpreadsheetAsync(token, spreadsheetId, ct);
        return new(true, spreadsheet.SpreadsheetId, spreadsheet.Properties?.Title);
    }

    public async Task<GoogleSpreadsheet> GetSpreadsheetAsync(string token, string spreadsheetId, CancellationToken ct = default)
    {
        Validate(spreadsheetId, nameof(spreadsheetId));
        using var request = Request(token, HttpMethod.Get,
            $"{Uri.EscapeDataString(spreadsheetId)}?fields=spreadsheetId,spreadsheetUrl,properties(title),sheets(properties(sheetId,title,index,gridProperties(rowCount,columnCount)))");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GoogleSpreadsheet>(Json, ct)
            ?? throw new InvalidOperationException("Google Sheets returned an empty spreadsheet.");
    }

    public async Task<GoogleSheetValues> GetValuesAsync(string token, string spreadsheetId, string range,
        string valueRenderOption = "FORMATTED_VALUE", CancellationToken ct = default)
    {
        Validate(spreadsheetId, nameof(spreadsheetId));
        Validate(range, nameof(range));
        if (valueRenderOption is not ("FORMATTED_VALUE" or "UNFORMATTED_VALUE" or "FORMULA"))
            throw new ArgumentException("Unsupported value render option.", nameof(valueRenderOption));
        using var request = Request(token, HttpMethod.Get,
            $"{Uri.EscapeDataString(spreadsheetId)}/values/{Uri.EscapeDataString(range)}?majorDimension=ROWS&valueRenderOption={valueRenderOption}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GoogleSheetValues>(Json, ct)
            ?? throw new InvalidOperationException("Google Sheets returned an empty values response.");
    }

    public async Task<GoogleSheetValues> UpdateValuesAsync(string token, string spreadsheetId, string range,
        IReadOnlyList<IReadOnlyList<object?>> values, string valueInputOption = "USER_ENTERED",
        CancellationToken ct = default)
    {
        Validate(spreadsheetId, nameof(spreadsheetId));
        Validate(range, nameof(range));
        ArgumentNullException.ThrowIfNull(values);
        if (valueInputOption is not ("RAW" or "USER_ENTERED"))
            throw new ArgumentException("valueInputOption must be RAW or USER_ENTERED.", nameof(valueInputOption));
        using var request = Request(token, HttpMethod.Put,
            $"{Uri.EscapeDataString(spreadsheetId)}/values/{Uri.EscapeDataString(range)}?valueInputOption={valueInputOption}");
        request.Content = JsonContent.Create(new { range, majorDimension = "ROWS", values }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GoogleSheetValues>(Json, ct)
            ?? throw new InvalidOperationException("Google Sheets returned an empty update response.");
    }

    public async Task<GoogleSheetValues> AppendValuesAsync(string token, string spreadsheetId, string range,
        IReadOnlyList<IReadOnlyList<object?>> values, string valueInputOption = "USER_ENTERED",
        string insertDataOption = "INSERT_ROWS", CancellationToken ct = default)
    {
        Validate(spreadsheetId, nameof(spreadsheetId));
        Validate(range, nameof(range));
        ArgumentNullException.ThrowIfNull(values);
        if (valueInputOption is not ("RAW" or "USER_ENTERED"))
            throw new ArgumentException("valueInputOption must be RAW or USER_ENTERED.", nameof(valueInputOption));
        if (insertDataOption is not ("OVERWRITE" or "INSERT_ROWS"))
            throw new ArgumentException("insertDataOption must be OVERWRITE or INSERT_ROWS.", nameof(insertDataOption));
        using var request = Request(token, HttpMethod.Post,
            $"{Uri.EscapeDataString(spreadsheetId)}/values/{Uri.EscapeDataString(range)}:append?valueInputOption={valueInputOption}&insertDataOption={insertDataOption}");
        request.Content = JsonContent.Create(new { range, majorDimension = "ROWS", values }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GoogleSheetValues>(Json, ct)
            ?? throw new InvalidOperationException("Google Sheets returned an empty append response.");
    }

    public async Task<GoogleSpreadsheet> CreateSpreadsheetAsync(string token, string title,
        IReadOnlyList<string>? sheetTitles = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Spreadsheet title is required.", nameof(title));
        var payload = new Dictionary<string, object?> { ["properties"] = new { title } };
        if (sheetTitles is { Count: > 0 })
        {
            if (sheetTitles.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Sheet titles cannot be empty.", nameof(sheetTitles));
            payload["sheets"] = sheetTitles.Select((name, index) => new { properties = new { title = name, index } }).ToArray();
        }
        using var request = Request(token, HttpMethod.Post, "");
        request.Content = JsonContent.Create(payload, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GoogleSpreadsheet>(Json, ct)
            ?? throw new InvalidOperationException("Google Sheets returned an empty creation response.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Google OAuth access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void Validate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty value is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Google Sheets returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record GoogleSheetsConnection(bool Connected, string? SpreadsheetId, string? Title);
public sealed record GoogleSpreadsheet(string? SpreadsheetId, string? SpreadsheetUrl, GoogleSpreadsheetProperties? Properties,
    IReadOnlyList<GoogleSheet>? Sheets);
public sealed record GoogleSpreadsheetProperties(string? Title);
public sealed record GoogleSheet(GoogleSheetProperties? Properties);
public sealed record GoogleSheetProperties(int SheetId, string? Title, int Index, GoogleGridProperties? GridProperties);
public sealed record GoogleGridProperties(int RowCount, int ColumnCount);
public sealed record GoogleSheetValues(
    [property: JsonPropertyName("range")] string? Range,
    [property: JsonPropertyName("majorDimension")] string? MajorDimension,
    [property: JsonPropertyName("values")] IReadOnlyList<IReadOnlyList<JsonElement>>? Values,
    [property: JsonPropertyName("updatedRange")] string? UpdatedRange,
    [property: JsonPropertyName("updatedRows")] int? UpdatedRows,
    [property: JsonPropertyName("updatedColumns")] int? UpdatedColumns,
    [property: JsonPropertyName("updatedCells")] int? UpdatedCells);
