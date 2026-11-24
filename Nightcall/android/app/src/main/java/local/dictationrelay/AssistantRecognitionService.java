package local.dictationrelay;

import android.content.Intent;
import android.speech.RecognitionService;

public final class AssistantRecognitionService extends RecognitionService {
    @Override protected void onStartListening(Intent recognizerIntent, Callback listener) { }
    @Override protected void onCancel(Callback listener) { }
    @Override protected void onStopListening(Callback listener) { }
}
