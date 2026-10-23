# BestCx contributor instructions

- All UI labels, messages, fixtures, source prose, and documentation must be in English. Use left-to-right layout, English digits, Gregorian dates, and UTC report boundaries.
- Keep complaints and customer experience separate. `feedback_type == "complaint"` identifies complaints; sentiment never changes workspace membership.
- Apply scope on the server before pagination, counts, and aggregation. Complaint views omit satisfaction ratings, CSAT, CES, and NPS.
- Use shared grid, filter, table, and pagination components. Navigation entries require icons.
- Journey maps distinguish current/future states and evidence/hypotheses. Do not invent research evidence or require event tracking to create a map.
- Account actions and notifications belong in the page title row. Profile updates are limited to the current account.
- Keep generation, decision, and transcription model settings separate. Request English generated text and preserve the spoken language during transcription.
- Never commit secrets, populated environment files, production data, recordings, logs, or database dumps. Never import another installation's Git history or runtime volumes.
- Generate private credentials with `scripts/setup.py`. Keep Compose resources under the `bestcx` project and do not alter other installations.
- Run checks appropriate to your change. Honor any user instruction not to run tests. Publish only reviewed source files.
