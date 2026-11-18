# Optional profile-wide unanswered-message limit

Standalone feature tracked in [#300](https://github.com/repasscloud/wanderconnect/issues/300) (and [#256](https://github.com/repasscloud/wanderconnect/issues/256), whose default of 5 this supersedes). Requirements preserved from the earlier mixed planning document. **Implemented** on the API, web and app; implementation choices are DEFAULT in [D380](../delivery/DECISIONS.md#d380--optional-unanswered-message-limit-18-november-2026).

## Objective

Give members a voluntary messaging boundary to reduce repeated unanswered messages without forcing a member to block or disconnect someone, while preserving normal adult communication.

## Key decision: OFF by default

**Default for all new and existing accounts: Off (unlimited).** A member may enable the profile-wide setting and select **3, 5 or 10** consecutive unanswered messages. Do **not** set the default to 5; earlier draft language suggesting that was superseded by the explicit decision to default **Off**.

## Requirements

- One **global profile/privacy setting per recipient**; it applies to that member's incoming messages across all one-to-one conversations. **No per-conversation overrides**, no relationship-based algorithm and no time-based reset.
- If Member B enables a limit of 5, Member A may send five consecutive messages to B without a response. The sixth is rejected until B replies at least once, even if the five messages were sent over weeks or years.
- The converse direction is assessed independently using Member A's own chosen receiving limit.
- Any successful recipient reply—even a one-word reply—resets the other member's consecutive unanswered-message count for that conversation.
- Opening, reading, marking as unread, editing or deleting messages does not reset the counter. Message attachment types count in a consistent way; system messages, typing/read receipts do not.
- Changing a recipient's profile setting takes effect immediately, including on existing conversations. Disabling the limit permits messaging subject to all other access and safety rules; re-enabling evaluates the existing unanswered sequence.
- The Swoovr API enforces it atomically across web, iOS and Android; clients show a neutral message such as: "You've reached the unanswered message limit for this conversation. You can send more when they reply."
- Don't disclose the exact recipient preference unless there is an established UX reason; don't pressure the recipient to reply.
- Preserve reporting, blocking, disconnecting and existing membership-tier restrictions; this is a safety/comfort feature available to **Free and paid members**.

## Tests / acceptance

Evidence: `tests/Swoovr.Checks/MessagingChecks.cs` and `NotificationChecks.cs` (policy, wire values, shared wording), `tests/Swoovr.Checks/UnansweredLimitPersistenceChecks.cs` (PostgreSQL: the stored setting and the run the send routes read, including concurrent sends under the conversation lock), and the owned browser suite `tests/messaging/unanswered-limit.browser.cjs` (API end to end and the web composer).

- [x] Default is Off for new and existing members, with no messaging restriction imposed by this feature.
- [x] Off / 3 / 5 / 10 settings persist across devices and sessions (stored on the server; other values are refused by the API and the database).
- [x] Message at threshold succeeds; next unanswered message is rejected.
- [x] Reply resets allowance; read receipt and elapsed time do not (Swoovr has no read receipts; reading a chat is checked).
- [x] Text, media and supported attachments count; system messages do not (text and photos are the only member messages today; farewells and system events don't count).
- [x] Independent counts in each direction and each conversation.
- [x] Edit/delete/clock-wait workarounds fail (messages can't be edited; delete for both, hiding and moderation removal keep the run).
- [x] Changing 10 to 3 immediately restricts someone with 5 unanswered messages; turning Off permits messages again.
- [x] Re-enabling does not inadvertently wipe unanswered history.
- [x] Concurrent sends cannot bypass limit; rejected sends do not become persisted messages.
- [x] Web and app give the same neutral sender feedback (shared `Swoovr.Client.MessageText`); the app's screens are built but not run on a device here (web first).

## Clarifications before implementation (resolved, D380)

- Data model: one conversation per connected pair; messages are never edited, and delete for both, hiding, screening and moderation removal keep the row. The run is counted from the stored messages (bounded, indexed), so no counter can drift and no backfill is needed.
- Scope: one-to-one member conversations only (Swoovr has no group chats or reactions). Farewells and system events don't count; support and staff never write in member conversations.
- Photos count, and reset the other direction, from acceptance, whatever their screening outcome (DEFAULT).
- Existing messaging limits (30 a minute, 20 photos a day) are unchanged.

