/** List the Jev models available to this account. Run: npm run models */
import { TypeSafeClient } from "@typesafe-ai/sdk";

const client = new TypeSafeClient();
const models = await client.models.list();
for (const m of models) console.log(m.name, m);
