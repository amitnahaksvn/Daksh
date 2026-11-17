# WhatsApp automation rules

Settings → **Automation Rules** is a WHEN / IF / THEN builder. Rules are stored per connection
(`whatsappAutomationRules`), save immediately (they don't wait for *Save Changes*), and run on every incoming
message that isn't from a chat set to "ignore".

## WHEN (trigger — pick one)
A new message arrives · from a specific contact · in a specific group · contains a keyword · contains a
question · contains an attachment · arrives outside working hours (the window and timezone from General).

## IF (optional conditions — every one you fill in must match)
Contact/group is one of the selected chats · text contains a phrase · the message carries a detection
category (question, payment, meeting, OTP, …) · the chat is marked high priority.

## THEN (one or more actions)
| Action | What it does |
|---|---|
| Generate an AI response | Runs the AI reply and sends it. Bypasses scope, hours and cooldown (the rule is your explicit instruction) but never the Pause switch or a chat set to *Never auto-reply*. |
| Send an automatic response | Sends the fixed text you enter. |
| Create a reminder | Stores a reminder (due in an hour, or at "in 2 hours" / "tomorrow" if the message says so) and pops a notification when it comes due. |
| Extract an action item | Asks the AI for the single task the message asks of you and stores it as a task. |
| Notify me | Forces a notification even if filtered notifications would suppress it. |
| Summarize the conversation | Stores an AI summary of the last 30 messages. |
| Save the message | Stores the message in the saved list. |
| Add to my WhatsApp task list | Stores the message as a task. Daksha's main Tasks module is not available (its API is a stub), so tasks live in the list under **Message Intelligence**. |

If any matched rule sends a reply, the connection-wide auto-reply is skipped for that message so nothing
is answered twice. Rules run in creation order; several can match one message.

## Where results appear
Tasks, reminders, summaries, saved messages and detected items are all listed under **Message Intelligence →
What Daksha has picked up**, where tasks and reminders can be marked done or deleted.

## Limits
- Reminders are delivered as an in-app notification (and desktop notification if enabled) while Daksha is
  open — there is no mobile push.
- "Generate an AI response" and the two AI extraction actions use the Gemini quota; a rate-limited request
  is skipped and logged.
