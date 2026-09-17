/**
 * Minimal Jev (TypeSafe System One) example.
 *
 * Run:   npm start
 * Needs: TYPESAFE_API_KEY in the environment (see .env.example)
 *
 * Code owns the workflow; Jev supplies typed judgments. Independent
 * questions over the same state are asked together in one request.
 */
import { choice, noul, score, TypeSafeClient } from "@typesafe-ai/sdk";

const client = new TypeSafeClient(); // reads TYPESAFE_API_KEY, model defaults to jev-latest

const ticket = {
  subject: "Charged twice",
  body: "I was charged twice for my subscription this month. Please fix this ASAP.",
};

const { model, answers, usage } = await client.systemOne({
  state: { ticket },
  questions: {
    isBilling: noul("Is `ticket` about a billing or payment problem?"),
    tone: choice("What is the customer's tone in `ticket.body`?", {
      calm: null,
      frustrated: null,
      angry: null,
    }),
    urgency: score("How urgent is `ticket`?", [
      "No time pressure; can wait a week or more",
      "Should be handled within a few days",
      "Needs attention today",
    ]),
  },
});

console.log(`model: ${model}`);
console.log(`billing probability: ${answers.isBilling.noul.toFixed(3)}`);
console.log(
  `tone: ${answers.tone.choice} (confidence ${answers.tone.confidence.toFixed(2)})`,
  answers.tone.probabilities,
);
console.log(
  `urgency: ${answers.urgency.score.toFixed(2)} (confidence ${answers.urgency.confidence.toFixed(2)})`,
  answers.urgency.probabilities,
);
console.log(`tokens: in=${usage.input_tokens} out=${usage.output_tokens}`);

// Policy lives in code, not in the model.
if (answers.isBilling.noul > 0.8 && answers.urgency.score >= 1.5) {
  console.log("-> route to billing team, high priority");
}
