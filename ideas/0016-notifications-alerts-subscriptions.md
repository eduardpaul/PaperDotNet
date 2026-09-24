# 0016: Notifications, alerts and subscriptions

- **Status:** mapped
- **Area:** Platform / Notifications
- **Date:** 2026-09-24
- **Mapped to:** [features.md](../docs/features.md): NTF-01…06, API-06

## The idea

A notification system for users and integrations:

- **Alerts / subscriptions:** "notify me when this item, folder, list or
  search result changes" (like SharePoint alerts). Immediate or as a
  daily/weekly digest.
- **System notifications:**
  - task assigned or due soon
  - event reminders
  - @mentions
  - sharing (idea 0015)
  - OCR finished or failed
  - workflow steps (idea 0009)
- **Channels:**
  - in-app notification inbox (API)
  - email (SMTP)
  - webhooks
  - self-hosted push services (ntfy, Gotify)
  - later: mobile push and Web Push
- **User preferences:** per notification type and channel, quiet hours,
  digest frequency.

## Why / problem it solves

- Tasks and calendar need reminders to be useful.
- Users want to follow changes without checking manually.
- Extensions need one way to notify users instead of each building their own.

## Examples / references

- SharePoint alerts; Graph change notifications (webhook subscriptions,
  idea 0003).
- ntfy / Gotify: simple self-hosted push, popular with self-hosters.

## Notes

<!-- Open questions to settle during review:
     - Built on async after-events (idea 0012) from the outbox; scheduled
       reminders via Quartz.NET.
     - Channel providers are an extension point (Slack, Teams, Matrix,
       Telegram as extensions).
     - Self-hosting: in-app + email work with just an SMTP server configured;
       everything else optional.
     - Respect permissions: never notify about items the user can't see
       (re-check at send time).
     - Templates and localization of messages (user UI language preference).
     - Deduplicate and batch noisy changes (e.g. bulk updates → one message).
     - Delivery tracking, retries (Http.Resilience) and per-tenant rate limits.
     - API: `/me/notifications`, `/subscriptions` (Graph-style). -->
