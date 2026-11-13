"""Offline grammar gate for Unity-owned microphone audio; never opens a device."""
import io
import json
import threading
import wave


class UnityWake:
    def __init__(self, root):
        from desktop_wake import DesktopWake
        self.pack = DesktopWake(root, lambda _: None)
        self.lock = threading.Lock()
        self.lib = self.model = None

    def state(self):
        return self.pack.state()

    def install(self):
        with self.lock:
            return self.pack.install()

    def check(self, content, phrase):
        from desktop_wake import phrase_words
        phrase = phrase_words(phrase)
        if not isinstance(content, bytes) or not 44 <= len(content) <= 1_500_000:
            raise ValueError('Wake audio must be a WAV under 1.5 MB.')
        with wave.open(io.BytesIO(content), 'rb') as audio:
            if audio.getnchannels() != 1 or audio.getsampwidth() != 2 or audio.getcomptype() != 'NONE' or audio.getframerate() != 16000 or audio.getnframes() > 16_000 * 16:
                raise ValueError('Wake audio must be mono 16 kHz PCM16, at most 16 seconds.')
            pcm = audio.readframes(audio.getnframes())
            if len(pcm) != audio.getnframes() * 2:
                raise ValueError('Wake audio is truncated.')
        with self.lock:
            if not self.pack.state()['installed']:
                return {'available': False, 'detected': False}
            if self.lib is None:
                self.lib = self.pack._library()
            if self.model is None:
                self.model = self.lib.vosk_model_new(str(self.pack.model).encode('utf-8'))
                if not self.model:
                    raise RuntimeError('Offline wake model could not load.')
            for word in phrase.split():
                if self.lib.vosk_model_find_word(self.model, word.encode()) < 0:
                    raise ValueError('The offline wake model does not know a word in your wake phrase.')
            phrases = list(dict.fromkeys([phrase, 'wake up sakura', 'sakura wake up']))
            grammar = json.dumps(phrases + ['[unk]']).encode()
            recognizer = self.lib.vosk_recognizer_new_grm(self.model, 16000, grammar)
            if not recognizer:
                raise RuntimeError('Offline wake recognizer could not start.')
            try:
                detected = False
                for offset in range(0, len(pcm), 6400):
                    block = pcm[offset:offset + 6400]
                    complete = self.lib.vosk_recognizer_accept_waveform(recognizer, block, len(block))
                    raw = self.lib.vosk_recognizer_result(recognizer) if complete else self.lib.vosk_recognizer_partial_result(recognizer)
                    result = json.loads(raw.decode())
                    text = ' ' + result.get('text' if complete else 'partial', '') + ' '
                    detected |= any(' ' + item + ' ' in text for item in phrases)
                return {'available': True, 'detected': detected}
            finally:
                self.lib.vosk_recognizer_free(recognizer)

    def close(self):
        with self.lock:
            if self.lib and self.model:
                self.lib.vosk_model_free(self.model)
                self.model = None
