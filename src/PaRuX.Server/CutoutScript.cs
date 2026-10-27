namespace PaRuX.Server;

/// <summary>
/// The Python program the cutout engine runs: BiRefNet-lite through onnxruntime on the CPU (the GPU is left to games and
/// the media runtime), writing an RGBA PNG whose alpha is the model's mask. It prints one JSON line with the facts the C#
/// side re-checks (size, transparent share).
/// </summary>
internal static class CutoutScript
{
    internal const string Source = """
import sys, json, time, os
import numpy as np
from PIL import Image, ImageOps
import onnxruntime as ort

model_path, input_path, output_path = sys.argv[1], sys.argv[2], sys.argv[3]
Image.MAX_IMAGE_PIXELS = 40_000_000

start = time.monotonic()
src = Image.open(input_path)
src = ImageOps.exif_transpose(src)
has_alpha = src.mode in ('RGBA', 'LA') or (src.mode == 'P' and 'transparency' in src.info)
rgba = src.convert('RGBA')
width, height = rgba.size

# The model sees the picture on white, so existing transparency does not become black.
flat = Image.new('RGB', rgba.size, (255, 255, 255))
flat.paste(rgba, mask=rgba.split()[3])

SIZE = 1024
resized = flat.resize((SIZE, SIZE), Image.BILINEAR)
x = np.asarray(resized, dtype=np.float32) / 255.0
mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
x = ((x - mean) / std).transpose(2, 0, 1)[None, :, :, :].astype(np.float32)

options = ort.SessionOptions()
options.intra_op_num_threads = max(1, min(8, (os.cpu_count() or 4) - 1))
session = ort.InferenceSession(model_path, options, providers=['CPUExecutionProvider'])
input_name = session.get_inputs()[0].name
raw = session.run(None, {input_name: x})[0]
mask = np.asarray(raw, dtype=np.float32).reshape(-1)[-(SIZE * SIZE):].reshape(SIZE, SIZE)
if mask.min() < 0.0 or mask.max() > 1.0:
    mask = 1.0 / (1.0 + np.exp(-mask))
mask_image = Image.fromarray(np.clip(mask * 255.0 + 0.5, 0, 255).astype(np.uint8), mode='L').resize((width, height), Image.BILINEAR)

alpha = np.asarray(mask_image, dtype=np.uint8)
if has_alpha:
    alpha = np.minimum(alpha, np.asarray(rgba.split()[3], dtype=np.uint8))
out = rgba.copy()
out.putalpha(Image.fromarray(alpha, mode='L'))
out.save(output_path, format='PNG')

transparent = float((alpha < 8).sum()) / float(alpha.size)
opaque = float((alpha > 247).sum()) / float(alpha.size)
print(json.dumps({'width': width, 'height': height, 'transparentRatio': round(transparent, 4),
                  'opaqueRatio': round(opaque, 4), 'seconds': round(time.monotonic() - start, 2),
                  'provider': session.get_providers()[0]}))
""";

    internal const string ProbeArguments =
        "-I -c \"import onnxruntime, numpy, PIL; print('PARUX_ORT:' + onnxruntime.__version__)\"";
}
