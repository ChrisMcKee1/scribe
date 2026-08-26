namespace Scribe.Evals.Benchmark;

/// <summary>
/// A JSON schema a response must conform to, expressed in primitives so a caller does not have to
/// reference the OpenAI client types to ask for structured output.
/// </summary>
/// <remarks>
/// Added for tools/Scribe.StyleEval's LLM judge. A judge whose verdicts are prose has to be parsed
/// with a regex and cannot be aggregated, so its scores are forced through a schema instead. Keeping
/// the shape as strings here means the style suite never takes a direct dependency on
/// <c>OpenAI.Responses</c>, and it means the schema itself lives next to the prompt it belongs to.
/// </remarks>
/// <param name="Name">Schema name sent to the service. Identifier-shaped, no spaces.</param>
/// <param name="SchemaJson">The JSON Schema document, as JSON text.</param>
/// <param name="Description">One line telling the model what the object is for.</param>
/// <param name="Strict">
/// True to ask the service to guarantee conformance. Strict mode requires every object to set
/// <c>additionalProperties</c> false and to list every property in <c>required</c>, so a schema that
/// does not do both will be rejected by the service rather than silently relaxed.
/// </param>
internal sealed record JsonSchemaFormat(
    string Name,
    string SchemaJson,
    string? Description = null,
    bool Strict = true);
