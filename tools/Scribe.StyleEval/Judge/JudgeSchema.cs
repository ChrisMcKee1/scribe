namespace Scribe.StyleEval.Judge;

/// <summary>
/// The JSON schema every judge verdict is forced through.
/// </summary>
/// <remarks>
/// <para>
/// Structured output rather than prose is the difference between a judge whose numbers can be
/// aggregated and a judge whose opinions have to be read one at a time. The service is asked for
/// strict conformance, which is why every object here sets <c>additionalProperties</c> false and
/// lists every property in <c>required</c>: strict mode rejects a schema that does not.
/// </para>
/// <para>
/// No <c>minimum</c> or <c>maximum</c> appears on the integer scores. Strict structured output
/// supports a subset of JSON Schema that does not include numeric range keywords, and a schema
/// carrying one is refused by the service rather than quietly relaxed. The ranges are stated in the
/// field descriptions and clamped when the verdict is parsed.
/// </para>
/// <para>
/// Every finding carries a span, and the span is required rather than optional on purpose. An
/// unquoted complaint cannot be checked against the input or the output, so it cannot be told apart
/// from a hallucination, and <see cref="Grounding"/> verifies each one before the finding is allowed
/// to count toward any number in the report.
/// </para>
/// </remarks>
internal static class JudgeSchema
{
    /// <summary>Schema name sent to the service.</summary>
    public const string Name = "scribe_style_verdict";

    /// <summary>One line telling the model what it is filling in.</summary>
    public const string Description =
        "A structural and quality audit of one text transformation, with every finding quoted from " +
        "the input or the output.";

    /// <summary>
    /// Bumped whenever the schema or the prompt changes in a way that makes an old verdict
    /// incomparable. It is part of the content hash, so a bump invalidates the cache rather than
    /// mixing two generations of verdict into one report.
    /// </summary>
    public const string Version = "judge-1";

    /// <summary>The schema document.</summary>
    public const string Json =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["structureVerdict", "missedOpportunities", "unwarrantedStructure", "fidelityIssues", "quality"],
          "properties": {
            "structureVerdict": {
              "type": "string",
              "enum": ["under-structured", "appropriate", "over-structured"],
              "description": "One word for the output as a whole, judged against what the content warranted."
            },
            "missedOpportunities": {
              "type": "array",
              "description": "Structure the content genuinely warranted that the output does not have. Empty when there is none.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["kind", "inputSpan", "severity", "detectionSignal", "groundTruth", "explanation"],
                "properties": {
                  "kind": {
                    "type": "string",
                    "enum": ["bold", "bulleted-list", "numbered-list", "table", "code", "heading", "link", "block-quote", "definition-list"],
                    "description": "The markup the content warranted."
                  },
                  "inputSpan": {
                    "type": "string",
                    "description": "The exact words from the INPUT that warranted it, copied character for character. Never paraphrased, never invented."
                  },
                  "severity": {
                    "type": "string",
                    "enum": ["minor", "moderate", "major"],
                    "description": "major: a reader loses the point without it. moderate: clearly worse without it. minor: a careful editor would add it, the text survives without it."
                  },
                  "detectionSignal": {
                    "type": "string",
                    "description": "Which bullet of the Detection rules justifies it, in your own words, one clause."
                  },
                  "groundTruth": {
                    "type": "string",
                    "enum": ["confirms", "silent", "contradicts"],
                    "description": "Whether the GROUND TRUTH block explicitly backs this finding, says nothing about it, or argues against it. Answer honestly: silent is an acceptable answer."
                  },
                  "explanation": {
                    "type": "string",
                    "description": "One sentence: what the reader loses because the structure is absent."
                  }
                }
              }
            },
            "unwarrantedStructure": {
              "type": "array",
              "description": "Structure present in the output that the content did not warrant, or that a Restraint ceiling forbids. Empty when there is none.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["kind", "outputSpan", "severity", "restraintRule", "explanation"],
                "properties": {
                  "kind": {
                    "type": "string",
                    "enum": ["bold", "bulleted-list", "numbered-list", "table", "code", "heading", "link", "block-quote", "definition-list"],
                    "description": "The markup that should not be there."
                  },
                  "outputSpan": {
                    "type": "string",
                    "description": "The exact words from the OUTPUT carrying it, copied character for character."
                  },
                  "severity": {
                    "type": "string",
                    "enum": ["minor", "moderate", "major"],
                    "description": "major: the structure damages the meaning. moderate: clutter a reader notices. minor: defensible but not earned."
                  },
                  "restraintRule": {
                    "type": "string",
                    "description": "Which Restraint ceiling it breaks, in your own words, one clause."
                  },
                  "explanation": {
                    "type": "string",
                    "description": "One sentence saying why the content did not warrant it."
                  }
                }
              }
            },
            "fidelityIssues": {
              "type": "array",
              "description": "Any fact, number, name, commitment, caveat or question changed, softened, sharpened, added or dropped. Empty when there is none.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["type", "inputSpan", "outputSpan", "severity", "explanation"],
                "properties": {
                  "type": {
                    "type": "string",
                    "enum": ["dropped", "added", "changed", "softened", "sharpened"],
                    "description": "What happened to it."
                  },
                  "inputSpan": {
                    "type": "string",
                    "description": "The exact words from the INPUT this concerns, copied character for character. An empty string only when the type is added."
                  },
                  "outputSpan": {
                    "type": "string",
                    "description": "The exact words from the OUTPUT this concerns, copied character for character. An empty string only when the type is dropped."
                  },
                  "severity": {
                    "type": "string",
                    "enum": ["minor", "moderate", "major"],
                    "description": "major: the output now says something the author did not say. moderate: a real loss of information. minor: a shade of emphasis."
                  },
                  "explanation": {
                    "type": "string",
                    "description": "One sentence naming what changed."
                  }
                }
              }
            },
            "quality": {
              "type": "object",
              "additionalProperties": false,
              "required": ["goal", "register", "clarity", "fidelity", "overall", "wouldShipAsIs", "verdict"],
              "properties": {
                "goal": {
                  "type": "integer",
                  "description": "0 to 100. How well the output achieves this action's own stated goal, answered against the goal question you were given."
                },
                "register": {
                  "type": "integer",
                  "description": "0 to 100. Is the tone right for the destination, without padding, slang or ceremony the author did not use."
                },
                "clarity": {
                  "type": "integer",
                  "description": "0 to 100. Sentence by sentence, is this easier to read than the input while still sounding like the same person."
                },
                "fidelity": {
                  "type": "integer",
                  "description": "0 to 100. 100 when every fact, number, name, commitment, caveat and question survives at the strength the author wrote it."
                },
                "overall": {
                  "type": "integer",
                  "description": "0 to 100, holistic. Not the mean. An output a careful human editor would refuse to send scores below 50 whatever the other numbers say."
                },
                "wouldShipAsIs": {
                  "type": "boolean",
                  "description": "True when a careful human editor would paste this into the destination without touching it."
                },
                "verdict": {
                  "type": "string",
                  "description": "One or two sentences. The single most useful thing to say about this output."
                }
              }
            }
          }
        }
        """;
}
