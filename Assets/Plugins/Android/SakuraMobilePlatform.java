package app.sakura.unity;

import android.app.Activity;
import android.content.Intent;
import android.app.Fragment;
import android.os.Bundle;
import android.speech.SpeechRecognizer;
import android.speech.RecognitionListener;
import android.speech.RecognizerIntent;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.speech.tts.TextToSpeech;
import android.speech.tts.UtteranceProgressListener;
import android.util.Base64;
import java.security.KeyStore;
import java.util.Locale;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import com.unity3d.player.UnityPlayer;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;

public final class SakuraMobilePlatform {
    private static final String ALIAS = "sakura-unity-settings";
    private static TextToSpeech speech;
    private static SpeechRecognizer recognizer;
    private static String receiver = "Sakura Mobile";
    private static void send(String method, String message) { UnityPlayer.UnitySendMessage(receiver, method, message); }

    private static SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        if (!store.containsAlias(ALIAS)) {
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
            generator.generateKey();
        }
        return (SecretKey)store.getKey(ALIAS, null);
    }

    public static void save(Activity activity, String value) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key());
        String encoded = Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + ":" +
                Base64.encodeToString(cipher.doFinal(value.getBytes("UTF-8")), Base64.NO_WRAP);
        if (!activity.getSharedPreferences("sakura-unity-private", 0).edit().putString("settings", encoded).commit())
            throw new java.io.IOException("Settings could not be saved");
    }

    public static String load(Activity activity) throws Exception {
        String encoded = activity.getSharedPreferences("sakura-unity-private", 0).getString("settings", "");
        if (encoded.isEmpty()) return "";
        String[] parts = encoded.split(":", 2);
        if (parts.length != 2) throw new java.io.IOException("Saved settings are invalid");
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)));
        return new String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), "UTF-8");
    }

    public static void speak(Activity activity, String text, String language, String target) {
        receiver = target;
        activity.runOnUiThread(() -> {
            if (speech == null) {
                speech = new TextToSpeech(activity.getApplicationContext(), state -> {
                    if (state == TextToSpeech.SUCCESS) speak(activity, text, language, target);
                    else send("OnSpeechState", "error");
                });
                return;
            }
            speech.setLanguage("Hindi".equals(language) ? new Locale("hi", "IN") : Locale.ENGLISH);
            speech.setOnUtteranceProgressListener(new UtteranceProgressListener() {
                @Override public void onStart(String id) { send("OnSpeechState", "speaking"); }
                @Override public void onDone(String id) { send("OnSpeechState", "done"); }
                @Override public void onError(String id) { send("OnSpeechState", "error"); }
            });
            int result = speech.speak(text, TextToSpeech.QUEUE_FLUSH, null, "sakura-reply");
            if (result == TextToSpeech.ERROR) send("OnSpeechState", "error");
        });
    }

    public static void stop(Activity activity) { activity.runOnUiThread(() -> { if (speech != null) speech.stop(); }); }

    public static void listen(Activity activity, String language, String target) {
        receiver = target;
        activity.runOnUiThread(() -> {
            if (!SpeechRecognizer.isRecognitionAvailable(activity)) { send("OnRecognitionError", "Speech recognition is unavailable on this phone."); return; }
            if (recognizer != null) { recognizer.cancel(); recognizer.destroy(); }
            recognizer = SpeechRecognizer.createSpeechRecognizer(activity);
            recognizer.setRecognitionListener(new RecognitionListener() {
                @Override public void onReadyForSpeech(Bundle b) { }
                @Override public void onBeginningOfSpeech() { }
                @Override public void onRmsChanged(float rms) { }
                @Override public void onBufferReceived(byte[] bytes) { }
                @Override public void onEndOfSpeech() { }
                @Override public void onPartialResults(Bundle b) { }
                @Override public void onEvent(int event, Bundle b) { }
                @Override public void onError(int error) { send("OnRecognitionError", "Speech recognition stopped (" + error + "). Tap Mic to retry."); }
                @Override public void onResults(Bundle b) {
                    java.util.ArrayList<String> results = b.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION);
                    if (results != null && !results.isEmpty()) send("OnTranscript", results.get(0));
                    else send("OnRecognitionError", "No speech was recognised. Tap Mic to retry.");
                }
            });
            Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
            intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
            intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, "Hindi".equals(language) ? "hi-IN" : "en-IN");
            recognizer.startListening(intent);
        });
    }

    public static void stopListening(Activity activity) { activity.runOnUiThread(() -> { if (recognizer != null) { recognizer.cancel(); recognizer.destroy(); recognizer = null; } }); }

    public static void chooseModel(Activity activity, String target) {
        receiver = target;
        activity.runOnUiThread(() -> {
            if (activity.getFragmentManager().findFragmentByTag("sakura-file-picker") != null) return;
            activity.getFragmentManager().beginTransaction().add(new ModelPicker(), "sakura-file-picker").commit();
        });
    }

    public static void finishImport(Activity activity, String path) throws Exception {
        File file = new File(path).getCanonicalFile();
        File folder = file.getParentFile();
        if (folder == null || !folder.getName().startsWith("sakura-import-") || !folder.getParentFile().equals(activity.getCacheDir().getCanonicalFile()))
            throw new java.io.IOException("Invalid import cache path");
        if (file.exists() && !file.delete()) throw new java.io.IOException("Cannot clear temporary import");
        folder.delete();
    }

    public static final class ModelPicker extends Fragment {
        @Override public void onCreate(Bundle state) {
            super.onCreate(state);
            if (state != null) return;
            Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
            intent.addCategory(Intent.CATEGORY_OPENABLE);
            intent.setType("*/*");
            startActivityForResult(intent, 731);
        }

        @Override public void onActivityResult(int request, int result, Intent data) {
            if (request != 731) return;
            Activity activity = getActivity();
            if (result == Activity.RESULT_OK && data != null && data.getData() != null) {
                android.net.Uri uri = data.getData();
                new Thread(() -> {
                    File staging = null;
                    try {
                        String name = "model.zip";
                        try (android.database.Cursor cursor = activity.getContentResolver().query(uri, new String[]{android.provider.OpenableColumns.DISPLAY_NAME}, null, null, null)) {
                            if (cursor != null && cursor.moveToFirst()) name = cursor.getString(0);
                        }
                        name = name.replaceAll("[/\\\\]", "_");
                        String lower = name.toLowerCase(Locale.ROOT);
                        if (!lower.endsWith(".zip") && !lower.endsWith(".vrm")) throw new java.io.IOException("Choose a complete Live2D ZIP or VRM file.");
                        staging = new File(activity.getCacheDir(), "sakura-import-" + java.util.UUID.randomUUID());
                        if (!staging.mkdir()) throw new java.io.IOException("Cannot create import folder");
                        File file = new File(staging, name);
                        try (InputStream input = activity.getContentResolver().openInputStream(uri); FileOutputStream output = new FileOutputStream(file)) {
                            if (input == null) throw new java.io.IOException("Cannot read selected file");
                            byte[] buffer = new byte[65536]; long total = 0; int count;
                            while ((count = input.read(buffer)) != -1) {
                                total += count;
                                if (total > 512L * 1024 * 1024) throw new java.io.IOException("Model package exceeds 512 MB.");
                                output.write(buffer, 0, count);
                            }
                        }
                        send("OnModelSelected", file.getAbsolutePath());
                    } catch (Exception error) {
                        if (staging != null) { File[] files = staging.listFiles(); if (files != null) for (File file : files) file.delete(); staging.delete(); }
                        send("OnImportError", "Model import failed. " + error.getMessage());
                    }
                }, "Sakura model picker").start();
            }
            activity.getFragmentManager().beginTransaction().remove(this).commit();
        }
    }
}
