# Phone turns and account selection

The desktop starts an auxiliary native app server without `features.prefer_mxc`.
Its built-in remote-control transport receives phone JSON-RPC requests internally,
so those requests never cross ProtocolBridge's stdio account router. Consequently
the receiver's own login used to pay for all remote turns, even on Auto chats.

`remote_inference.py` routes native account workers and the auxiliary receiver through a process-local Responses endpoint. The provider identity stays `openai`, preserving the native chat-list filter and existing chat visibility.
The native server keeps its own login, pairing, approvals, events and reconnection.
Inference uses HTTP with full context and credentials from a selected profile:
explicit conversation assignments win; Auto uses quota density and idle-account
preference, matching the desktop router. No quota means an explicit routing error,
not fallback to the receiver's exhausted login. Turn metadata, when supplied by the
engine, keeps requests within that turn on the selected account.

The adapter listens on loopback at an ephemeral port and requires a random capability
passed in the child endpoint configuration. It rejects browser origins and other hosts. Only
Responses, Responses compaction, and standalone web search (`alpha/search`) requests can be forwarded, to the fixed existing
ChatGPT Codex endpoint. Redirects are disabled. Credentials and prompts are never
written to diagnostic logs; `bridge/remote-routing.jsonl` records conversation ID,
turn ID, selected profile ID, and upstream status. Credentials remain in profile
homes and the GUI router must be running to supply account state.

This changes account-worker and auxiliary app-server launches made by the multi-account launcher,
not unrelated CLI or separately launched app-server processes. Existing receivers
must reconnect/restart to load the provider. The phone app needs no modification.

Checks: `python -B -m unittest test_remote_inference -v`. Set `CA_TEST_ENGINE` to the
installed engine executable to include an isolated native engine test: the model
request ends at a fake upstream and does not consume model quota.
