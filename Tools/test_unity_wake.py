import io
import json
import sys
import types
import unittest
import wave
from unittest.mock import patch
from unity_wake import UnityWake


def wav(rate=16000):
    output = io.BytesIO()
    with wave.open(output, 'wb') as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(rate)
        audio.writeframes(b'\0\0' * 6400)
    return output.getvalue()


class WakeTests(unittest.TestCase):
    def detector(self, installed=True, text='hey sakura'):
        from pathlib import Path
        lib = types.SimpleNamespace(
            vosk_model_new=lambda _: 1, vosk_model_find_word=lambda *_: 1,
            vosk_recognizer_new_grm=lambda *_: 2,
            vosk_recognizer_accept_waveform=lambda *_: 0,
            vosk_recognizer_partial_result=lambda _: json.dumps({'partial': text}).encode(),
            vosk_recognizer_free=lambda _: None, vosk_model_free=lambda _: None)
        pack = types.SimpleNamespace(model=Path('mock'), state=lambda: {'installed': installed}, _library=lambda: lib)
        self.module = types.SimpleNamespace(DesktopWake=lambda *_: pack, phrase_words=lambda value: value)
        with patch.dict(sys.modules, desktop_wake=self.module):
            detector = UnityWake('.')
        return detector

    def check(self, detector, content):
        with patch.dict(sys.modules, desktop_wake=self.module):
            return detector.check(content, 'hey sakura')

    def test_offline_gate_and_word_boundaries(self):
        detector = self.detector(text='hey sakura')
        self.assertEqual(self.check(detector, wav()), {'available': True, 'detected': True})
        detector.close()
        self.assertIsNone(detector.model)
        detector = self.detector(text='hey sakurabelle')
        self.assertFalse(self.check(detector, wav())['detected'])

    def test_missing_pack_does_not_load_model(self):
        detector = self.detector(installed=False)
        self.assertEqual(self.check(detector, wav()), {'available': False, 'detected': False})
        self.assertIsNone(detector.lib)

    def test_audio_bounds(self):
        detector = self.detector()
        for content in (b'bad', b'x' * 1_500_001, wav(48000)):
            with self.assertRaises(ValueError):
                self.check(detector, content)


if __name__ == '__main__':
    unittest.main()
