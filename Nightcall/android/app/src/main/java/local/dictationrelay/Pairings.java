package local.dictationrelay;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.util.UUID;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import org.json.*;

final class Pairings {
    private final SharedPreferences prefs;
    private static final String ALIAS = "dictation-relay-pairing";
    Pairings(Context context) { prefs = context.getSharedPreferences("pairings", Context.MODE_PRIVATE); }
    boolean hasAny() { return !prefs.getString("computers", "").isEmpty(); }
    String phoneId() {
        String id = prefs.getString("id", "");
        if (id.isEmpty()) { id = UUID.randomUUID().toString().replace("-", ""); if (!prefs.edit().putString("id", id).commit()) throw new IllegalStateException("Cannot save phone identity"); }
        return id;
    }
    private SecretKey key() throws Exception {
        KeyStore ks = KeyStore.getInstance("AndroidKeyStore"); ks.load(null);
        if (!ks.containsAlias(ALIAS)) {
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
            generator.generateKey();
        }
        return (SecretKey)ks.getKey(ALIAS, null);
    }
    synchronized JSONArray all() throws Exception {
        String raw = prefs.getString("computers", ""); if (raw.isEmpty()) return new JSONArray();
        byte[] data = Base64.decode(raw, Base64.NO_WRAP);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, data, 0, 12));
        return new JSONArray(new String(cipher.doFinal(data, 12, data.length - 12), StandardCharsets.UTF_8));
    }
    synchronized void save(JSONObject pc) throws Exception {
        JSONArray old = all(), next = new JSONArray();
        next.put(pc);
        for (int i = 0; i < old.length(); i++) if (!old.getJSONObject(i).getString("id").equals(pc.getString("id"))) next.put(old.getJSONObject(i));
        saveAll(next);
    }
    synchronized void forgetAll() { prefs.edit().remove("computers").apply(); }
    private void saveAll(JSONArray data) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] encrypted = cipher.doFinal(data.toString().getBytes(StandardCharsets.UTF_8));
        byte[] packed = new byte[12 + encrypted.length]; System.arraycopy(cipher.getIV(), 0, packed, 0, 12); System.arraycopy(encrypted, 0, packed, 12, encrypted.length);
        if (!prefs.edit().putString("computers", Base64.encodeToString(packed, Base64.NO_WRAP)).commit()) throw new IllegalStateException("Cannot save pairing");
    }
}
