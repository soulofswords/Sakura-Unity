"""Isolated CPU MediaPipe process. Camera ownership remains in the native Unity client."""
import argparse
import base64
import io
import json
from pathlib import Path
import sys
import time


def points(values):
    return [dict(x=float(p.x), y=float(p.y), z=float(p.z), visibility=float(getattr(p, 'visibility', 1) or 1)) for p in values]


class Inference:
    def __init__(self, models):
        import mediapipe as mp
        from mediapipe.tasks import python
        from mediapipe.tasks.python import vision
        self.mp, self.vision, self.python, self.models = mp, vision, python, Path(models)
        self.face = vision.FaceLandmarker.create_from_options(vision.FaceLandmarkerOptions(
            base_options=python.BaseOptions(model_asset_path=str(self.models / 'face_landmarker.task')),
            running_mode=vision.RunningMode.VIDEO, num_faces=1,
            output_face_blendshapes=True, output_facial_transformation_matrixes=True))
        self.hand = self.pose = None
        self.stamp = 0

    def detect(self, request):
        import numpy as np
        from PIL import Image
        payload = base64.b64decode(request['jpeg'], validate=True)
        if not payload or len(payload) > 4 * 1024 * 1024:
            raise ValueError('Camera JPEG must be under 4 MB.')
        with Image.open(io.BytesIO(payload)) as decoded:
            if decoded.format != 'JPEG' or decoded.width != request['width'] or decoded.height != request['height'] or decoded.width > 1920 or decoded.height > 1080:
                raise ValueError('Camera dimensions do not match its bounded JPEG.')
            pixels = np.asarray(decoded.convert('RGB'))
        image = self.mp.Image(image_format=self.mp.ImageFormat.SRGB, data=pixels)
        self.stamp = max(self.stamp + 1, int(time.monotonic() * 1000))
        start = time.perf_counter()
        result = self.face.detect_for_video(image, self.stamp)
        face = {'landmarks': [], 'blendshapes': [], 'matrix': None}
        if result.face_landmarks:
            face = {'landmarks': points(result.face_landmarks[0]),
                    'blendshapes': [{'categoryName': category.category_name, 'score': float(category.score)} for category in (result.face_blendshapes[0] if result.face_blendshapes else [])],
                    'matrix': result.facial_transformation_matrixes[0].flatten(order='F').tolist() if result.facial_transformation_matrixes else None}
        if request.get('hands') and self.hand is None:
            self.hand = self.vision.HandLandmarker.create_from_options(self.vision.HandLandmarkerOptions(
                base_options=self.python.BaseOptions(model_asset_path=str(self.models / 'hand_landmarker.task')),
                running_mode=self.vision.RunningMode.VIDEO, num_hands=2))
        hands = []
        if request.get('hands'):
            hand_result = self.hand.detect_for_video(image, self.stamp)
            for index, landmarks in enumerate(hand_result.hand_landmarks):
                side = hand_result.handedness[index][0]
                hands.append({'side': side.category_name, 'confidence': float(side.score), 'landmarks': points(landmarks), 'world': points(hand_result.hand_world_landmarks[index])})
        if request.get('pose') and self.pose is None:
            self.pose = self.vision.PoseLandmarker.create_from_options(self.vision.PoseLandmarkerOptions(
                base_options=self.python.BaseOptions(model_asset_path=str(self.models / 'pose_landmarker_lite.task')),
                running_mode=self.vision.RunningMode.VIDEO, num_poses=1))
        pose = None
        if request.get('pose'):
            pose_result = self.pose.detect_for_video(image, self.stamp)
            if pose_result.pose_landmarks:
                pose = {'landmarks': points(pose_result.pose_landmarks[0]), 'world': points(pose_result.pose_world_landmarks[0])}
        elapsed = (time.perf_counter() - start) * 1000
        return {'face': face, 'hands': hands, 'pose': pose, 'handTrackingEnabled': bool(request.get('hands')),
                'trackingMetrics': {'processingMs': elapsed, 'processedFps': 1000 / max(1, elapsed)}}

    def close(self):
        for detector in (self.face, self.hand, self.pose):
            if detector is not None:
                detector.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--models', required=True)
    options = parser.parse_args()
    engine = None
    try:
        engine = Inference(options.models)
        print(json.dumps({'ready': True}), flush=True)
        while True:
            line = sys.stdin.buffer.readline(6 * 1024 * 1024)
            if not line:
                break
            try:
                if len(line) >= 6 * 1024 * 1024:
                    raise ValueError('Oversized camera message.')
                print(json.dumps({'frame': engine.detect(json.loads(line))}, allow_nan=False), flush=True)
            except Exception as error:
                print(json.dumps({'error': str(error)}), flush=True)
    except Exception as error:
        print(json.dumps({'error': str(error)}), flush=True)
        return 1
    finally:
        if engine is not None:
            engine.close()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
