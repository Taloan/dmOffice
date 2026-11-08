# Modelle (im Repo enthalten – keine Downloads zur Laufzeit)

| Datei | Zweck | Herkunft / Lizenz |
|---|---|---|
| `face_detection_yunet_2023mar.onnx` | Gesichtserkennung (bevorzugt) | OpenCV Zoo, `models/face_detection_yunet`, MIT (`LICENSE-yunet.txt`) |
| `face_recognition_sface_2021dec.onnx` | Gesichts-Fingerabdruck für `/faces` (128 Werte) | OpenCV Zoo, `models/face_recognition_sface`, Apache-2.0 (`LICENSE-opencv_zoo.txt`) |
| `haarcascade_frontalface_default.xml` | Fallback frontal | OpenCV `data/haarcascades` (4.x), Intel-Lizenz im Dateikopf |
| `haarcascade_profileface.xml` | Fallback Profil | wie oben |

Reihenfolge: YuNet → Haar → Salienz. `GET /health` zeigt unter `face_detector`, was aktiv ist.

## Trainingsdaten (Stand 2026-11-06)

Die Lizenzen oben sind die für die Modelldateien veröffentlichten. Die Rechte an den Trainingsdaten erfassen sie nicht.

- **YuNet:** Der [Commit der Modelldatei](https://github.com/ShiqiYu/libfacedetection.train/blob/a61a428929148171b488f024b5d6774f93cdbc13/tasks/task1/onnx/yunet.onnx) und die [Trainingsanleitung](https://github.com/ShiqiYu/libfacedetection.train) nennen WIDER FACE. Die [Seite des Datensatzes](http://shuoyang1213.me/WIDERFACE/) gibt als Lizenz „Creative Common License (cc by-nc-nd)“ an.
- **SFace:** Für diese Datei ist der Trainingsdatensatz nicht dokumentiert. Die [SFace-Anleitung](https://github.com/zhongyy/SFace/blob/main/SFace_torch/README.md) nennt allgemein CASIA-WebFace, VGGFace2 und MS1MV2 aus dem Data Zoo von InsightFace. [InsightFace](https://github.com/deepinsight/insightface#license) gibt seine Trainingsdaten und die damit trainierten Modelle nur „for non-commercial research purposes only“ frei. In [opencv_zoo #318](https://github.com/opencv/opencv_zoo/issues/318) verweist ein Mitglied des Projekts auf die Apache-2.0-Lizenz; welche Daten verwendet wurden, sagt es nicht. Auch [opencv_zoo #313](https://github.com/opencv/opencv_zoo/issues/313) und [SFace #9](https://github.com/zhongyy/SFace/issues/9) beantworten das nicht.

SHA-256 (Stand 2026-10-27):
```
8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4  face_detection_yunet_2023mar.onnx
0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79  face_recognition_sface_2021dec.onnx (2026-10-28)
0f7d4527844eb514d4a4948e822da90fbb16a34a0bbbbc6adc6498747a5aafb0  haarcascade_frontalface_default.xml
b39a4a3be45539db146a7fc1d3e761a292c196eb88421185e6a615b3055e612d  haarcascade_profileface.xml
```
