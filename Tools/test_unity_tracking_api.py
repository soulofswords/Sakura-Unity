import io
import unittest
from pathlib import Path

from unity_tracking_api import TrackingProcess


class TrackingChecks(unittest.TestCase):
    def test_frame_bounds(self):
        tracker = TrackingProcess(Path('.'))
        for data, width, height in [(b'', 640, 480), (b'x' * (4 * 1024 * 1024 + 1), 640, 480), (b'jpeg', 0, 480), (b'jpeg', 640, 1081)]:
            with self.assertRaises(ValueError):
                tracker.frame(data, width, height, False, False)

    def test_stopped_frame_rejected(self):
        tracker = TrackingProcess(Path('.'))
        with self.assertRaises(RuntimeError):
            tracker.frame(b'jpeg', 640, 480, False, False)

    def test_stop_is_idempotent(self):
        tracker = TrackingProcess(Path('.'))
        tracker.stop()
        tracker.stop()
        self.assertIsNone(tracker.process)


if __name__ == '__main__':
    unittest.main()
