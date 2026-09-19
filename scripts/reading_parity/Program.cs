using System.Text.Json;
using EdgeTtsOverlay.Services;

// reader-parity <inputs.json> <outputs.json>
// inputs: [{"text": ..., "reading": true, "terms": {...}}] → outputs: [{"prepared": ..., "segments": [...]}]
var inputs = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(args[0]))!;
var outputs = inputs.Select(c =>
{
    var prepared = TextProcessor.Prepare(c.text, c.reading, c.terms);
    return new { prepared, segments = TextSegmenter.Split(prepared) };
}).ToList();
File.WriteAllText(args[1], JsonSerializer.Serialize(outputs));

record Case(string text, bool reading, Dictionary<string, string> terms);
