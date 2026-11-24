package local.dictationrelay;

final class SessionCommands {
    static String forConnection(String kind, boolean connected) {
        if (!connected && ("toggle".equals(kind) || "finish".equals(kind) || "finishSend".equals(kind))) return "start";
        return kind;
    }
}
