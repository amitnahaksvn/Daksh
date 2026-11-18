# Automation

## What exists
- **Rules** (Daksha-side, `gmailRules`): conditions with AND/OR/NOT over sender, domain, recipient, CC, subject, body, attachment presence/type, label, importance,
  starred, AI category, urgency, date, keyword. Actions: apply/create/remove label, mark read/unread, star/unstar, mark important, archive, trash, forward (to a
  *verified* forwarding address only), create task/reminder, generate reply draft, notify, add to workflow (emits an event). Priority, stop-processing, enable/disable,
  duplicate, test against a sample, preview matches on recent mail, manual run, per-run history.
- **Safety:** every action is permission- and scope-checked per action. Rules that trash or forward never run automatically (and can't be saved as automatic); manual runs of
  them require explicit confirmation; automatic runs need the Autonomous profile (or the `runRules` override) and run at most once per rule+message.
- **Gmail filters** (Gmail-native) are managed separately under Gmail settings; they run inside Gmail.
- **Events outbox** (`gmailAutomationEvents`, one per account+trigger+subject): `new_email`, `important_email`, `urgent_email`, `email_from_selected_sender`, `email_with_attachment`,
  `invoice_detected`, `receipt_detected`, `meeting_invitation_detected`, `deadline_detected`, `action_item_detected`, `email_classified`, `email_sent`, `email_reply_received`,
  `no_reply_received`, `workflow:<name>`.
- Gmail operations (search, read, summarise, generate reply, drafts, send, forward, label, archive, mark, star, reminders/tasks) are available as REST endpoints.

## What does not exist (and why)
- `SchedulerService`/the central automation engine in `Daksh.Platform` throws "pending MongoDB rebuild", so nothing consumes the outbox yet and Gmail actions are not
  registered as workflow-engine nodes. Triggers `gmail_sync_completed` and `gmail_connection_failure` are not emitted (a notification is raised for failures instead).
- Tasks/reminders land in `gmailActionItems`, not the (stubbed) Tasks module.
- The five example workflows are **not implemented**: vendor/invoice-number/amount extraction, finance records, calendar events, travel-module integration and conditional
  multi-step flows need the engine and modules above. The detections they start from (invoice, meeting, deadlines, travel, no-reply) are in place.
- No-reply tracking: sent mail creates a follow-up due after the configured interval; the worker marks it due, notifies once and emits `no_reply_received`. Follow-up *drafts* are
  created on request; follow-ups are **never** sent automatically.
- Auto-replies: see the approval modes in [configuration-reference.md](configuration-reference.md). Default is "ask first". Sending needs mode AutoRules/AutoCategories + `autoSend`
  + Autonomous profile + approved rule/category + no "never commit" topic hit, and at most one automatic reply per thread.
