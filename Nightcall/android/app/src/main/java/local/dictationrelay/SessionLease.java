package local.dictationrelay;

/** Monotonic idle time; active dictation is never considered idle. */
final class SessionLease {
    static final long TIMEOUT_MS = 10 * 60_000;
    private boolean keep, active;
    private String state = "idle";
    private long deadline;
    synchronized void touch(long now) { deadline = now + TIMEOUT_MS; }
    synchronized void keep(boolean value, long now) { keep = value; touch(now); }
    synchronized void state(String next, long now) {
        boolean nextActive = "recording".equals(next) || "transcribing".equals(next);
        if (nextActive || !next.equals(state)) touch(now);
        state = next; active = nextActive;
    }
    synchronized boolean expired(long now) { return !keep && !active && now >= deadline; }
}
