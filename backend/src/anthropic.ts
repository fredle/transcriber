import Anthropic from "@anthropic-ai/sdk";

const MODEL = "claude-sonnet-5";

// Built lazily, not at module load: ANTHROPIC_API_KEY is optional until the
// AI feature is wired up, and the SDK throws in its constructor if the key
// is missing - doing that eagerly would crash the whole server on boot over
// one unused route.
let client: Anthropic | null | undefined;
function getClient(): Anthropic | null {
  if (client === undefined) {
    client = process.env.ANTHROPIC_API_KEY ? new Anthropic({ apiKey: process.env.ANTHROPIC_API_KEY }) : null;
  }
  return client;
}

export interface TranscriptLineForAi {
  speaker: string;
  speakerLabel?: string | null;
  text: string;
}

export async function askAboutMeeting(
  title: string,
  lines: TranscriptLineForAi[],
  question: string,
): Promise<string> {
  const client = getClient();
  if (!client) {
    throw new Error("AI Q&A isn't configured yet (no ANTHROPIC_API_KEY).");
  }

  const transcript = lines
    .map((l) => `[${l.speaker}${l.speakerLabel ? " " + l.speakerLabel : ""}] ${l.text}`)
    .join("\n");

  const message = await client.messages.create({
    model: MODEL,
    max_tokens: 1500,
    system:
      "You answer questions about a meeting transcript. Be concise and only " +
      "use information present in the transcript; say so plainly if the " +
      "transcript doesn't contain the answer.",
    messages: [
      {
        role: "user",
        content: `Meeting: ${title}\n\nTranscript:\n${transcript}\n\nQuestion: ${question}`,
      },
    ],
  });

  const textBlock = message.content.find((b) => b.type === "text");
  return textBlock && textBlock.type === "text" ? textBlock.text.trim() : "";
}
