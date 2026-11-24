package local.dictationrelay;

import android.service.voice.VoiceInteractionService;

public final class AssistantService extends VoiceInteractionService {
    static volatile boolean active;
    @Override public void onReady() { super.onReady(); active = true; }
    @Override public void onShutdown() { active = false; super.onShutdown(); }
}
