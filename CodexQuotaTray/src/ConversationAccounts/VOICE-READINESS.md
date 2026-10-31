# Voice startup readiness repair

The separate avatar/voice renderer was reporting `voice_disabled` while the main renderer reported voice enabled for the same authenticated login. Both renderers had successful configuration responses with `in_app_voice` enabled. The voice renderer's rollout SDK was Ready but returned `NoValues` for the voice rollout. The main renderer had recognized server bootstrap values.

A normal SDK `refreshValuesAsync()` in the voice renderer fetched recognized server values, enabled its voice capability, and reported an SDK localStorage cache-write quota error. The measured localStorage contents were small; clearing unrelated storage is not a fix. The fetched values remained valid in memory despite the failed cache write.

`voice-readiness.js` is loaded through the existing runtime attachment into both renderers. It checks that the rollout client's user and account match the current authenticated identity. Only a `NoValues` result triggers a normal SDK network refresh. It never forces a rollout value, grants voice eligibility, copies another account's rollout values, or changes quotas. Retries are limited to three attempts per authenticated identity with backoff. Explicit recognized server decisions are preserved.

The adapter targets exports in the currently installed desktop bundle. Unknown exports fail without disrupting ordinary chats. It starts no microphone capture or voice calls itself. `window.conversationVoiceReadiness.status()` reports repair state without exposing credentials or draft text.

Live verification: the existing SDK refresh changed the voice renderer from not capable to capable; after deployment, both main and voice renderers reported `ready` and voice capable. No end-to-end audio call was started for this verification. Separate transport errors and cross-account realtime call authentication are not proven fixed by this repair.

Run `node --test --test-isolation=none test_voice_readiness.cjs` to check missing-value recovery, identity matching, preserving server denials, bounded retries, and successful in-memory values after cache-write failure.
