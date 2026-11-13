# Native Agent and service readiness implementation plan

Approved scope: continue Docs/Migration.md toward the existing app's native feature/UI parity. Preserve original source and personal data. No cloud sync for PGR until requested.

1. Diagnose local chat with thread stacks and source runtime evidence. Correct the Unity bridge contract where it differs from the original client. Verify one real reply or report the precise remaining provider error.
2. Add native authenticated Agent API transport. Obtain the existing per-session token from the owner Agent page; retain it only in memory. Reject arbitrary routes and redirects; use original approval checks.
3. Build native Agent task navigation, model choice, workspace/goal input, task inspection, planning, approval/rejection, pause/resume/cancel, deletion and browser preview against existing routes. Do not bypass original policy or execute sample tasks on owner files.
4. Verify native UI and API calls with a temporary test workspace and harmless task, test missing/invalid responses, update acceptance ledger and commit the verified slice.
5. Continue tracking, animation, room and streaming integration from their owning existing services with separate regression checks. Preserve truthful pending hardware checks.
