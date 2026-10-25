package com.parux.android.ui.pairing

import android.Manifest
import android.content.pm.PackageManager
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.parux.android.core.pairing.PairingPayload
import com.parux.android.core.pairing.PairingRepository
import com.parux.android.core.pairing.PairingResult
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.launch

private enum class PairingStep { Intro, Scanning, Pairing, Failed }

/**
 * "Scan QR code" -> scan the QR from PaRuX Windows > Settings > Local Network > Pair a device.
 * Discovery for 1.0 is QR-only (no mDNS/DNS-SD): pairing identity comes from the short-lived Core
 * code plus the pinned HTTPS certificate fingerprint shown by the workstation.
 */
@Composable
fun PairingScreen(pairingRepository: PairingRepository, onPaired: () -> Unit) {
    var step by remember { mutableStateOf(PairingStep.Intro) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current

    var hasCameraPermission by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        )
    }
    val permissionLauncher = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestPermission()
    ) { granted ->
        hasCameraPermission = granted
        if (granted) step = PairingStep.Scanning
    }

    fun handleScan(payload: PairingPayload) {
        if (step == PairingStep.Pairing) return
        step = PairingStep.Pairing
        scope.launch {
            when (val result = pairingRepository.completePairing(payload)) {
                PairingResult.Success -> onPaired()
                is PairingResult.Failure -> {
                    errorMessage = result.message
                    step = PairingStep.Failed
                }
            }
        }
    }

    Box(Modifier.fillMaxSize()) {
        when (step) {
            PairingStep.Intro -> IntroContent(
                onScanClick = {
                    if (hasCameraPermission) step = PairingStep.Scanning
                    else permissionLauncher.launch(Manifest.permission.CAMERA)
                },
                onManualPayload = ::handleScan
            )
            PairingStep.Scanning -> if (hasCameraPermission) {
                QrScanner(
                    onScanned = ::handleScan,
                    onError = { message ->
                        errorMessage = message
                        step = PairingStep.Failed
                    }
                )
            } else {
                IntroContent(
                    onScanClick = { permissionLauncher.launch(Manifest.permission.CAMERA) },
                    onManualPayload = ::handleScan
                )
            }
            PairingStep.Pairing -> Column(
                Modifier.fillMaxSize(),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                CircularProgressIndicator()
                Text("Pairing with PaRuX PC…", Modifier.padding(top = 16.dp))
            }
            PairingStep.Failed -> Column(
                Modifier.fillMaxSize().padding(24.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                Text("Couldn't pair", style = MaterialTheme.typography.titleMedium)
                Text(errorMessage ?: "Unknown error", Modifier.padding(top = 8.dp, bottom = 24.dp))
                Button(onClick = { step = if (hasCameraPermission) PairingStep.Scanning else PairingStep.Intro }) { Text("Try again") }
                TextButton(onClick = { step = PairingStep.Intro }) { Text("Enter pairing details manually") }
            }
        }
    }
}

@Composable
private fun IntroContent(onScanClick: () -> Unit, onManualPayload: (PairingPayload) -> Unit) {
    Column(
        Modifier.fillMaxSize().padding(32.dp).verticalScroll(rememberScrollState()),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text("PaRuX", style = MaterialTheme.typography.displaySmall)
        Text(
            "Your Local AI",
            style = MaterialTheme.typography.bodyLarge,
            modifier = Modifier.padding(top = 4.dp, bottom = 32.dp)
        )
        Text(
            "On PaRuX Windows: Settings → Local Network → Pair a device. Scan the QR code it shows.",
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(bottom = 24.dp)
        )
        Button(onClick = onScanClick) { Text("Scan QR code") }

        // Production fallback for a device with no working camera. The exact same parser, TLS
        // certificate pinning and one-time pairing exchange are used as for a scanned QR.
        var showManualEntry by remember { mutableStateOf(false) }
        TextButton(onClick = { showManualEntry = !showManualEntry }, modifier = Modifier.padding(top = 24.dp)) {
            Text(if (showManualEntry) "Hide manual entry" else "No camera? Enter pairing details manually")
        }
        if (showManualEntry) {
            var endpoint by remember { mutableStateOf("") }
            var code by remember { mutableStateOf("") }
            var fingerprint by remember { mutableStateOf("") }
            Column(Modifier.padding(top = 8.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(
                    "Read these from PaRuX Windows → Settings → Local Network → Pair a device.",
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(bottom = 8.dp)
                )
                androidx.compose.material3.OutlinedTextField(
                    value = endpoint, onValueChange = { endpoint = it },
                    label = { Text("Endpoint (https://192.168.x.x:51124)") }, modifier = Modifier.padding(top = 4.dp)
                )
                androidx.compose.material3.OutlinedTextField(
                    value = code, onValueChange = { code = it },
                    label = { Text("Pairing code") }, modifier = Modifier.padding(top = 4.dp)
                )
                androidx.compose.material3.OutlinedTextField(
                    value = fingerprint, onValueChange = { fingerprint = it },
                    label = { Text("Certificate fingerprint") }, modifier = Modifier.padding(top = 4.dp)
                )
                Button(
                    onClick = {
                        onManualPayload(
                            PairingPayload(endpoint = endpoint.trim(), code = code.trim(), fp = fingerprint.trim())
                        )
                    },
                    modifier = Modifier.padding(top = 12.dp),
                    enabled = endpoint.isNotBlank() && code.isNotBlank() && fingerprint.isNotBlank()
                ) { Text("Pair") }
            }
        }

        // Debug-only shortcut for scripted/emulator pairing tests. BuildConfig.DEBUG strips this
        // control from release builds; production never accepts an unpinned debug transport.
        if (com.parux.android.BuildConfig.DEBUG) {
            var manualText by remember { mutableStateOf("") }
            Column(Modifier.padding(top = 24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text("Debug: paste pairing QR payload", style = MaterialTheme.typography.labelMedium)
                androidx.compose.material3.OutlinedTextField(
                    value = manualText,
                    onValueChange = { manualText = it },
                    modifier = Modifier.padding(top = 8.dp),
                    label = { Text("{\"v\":1,\"endpoint\":...}") }
                )
                Button(
                    onClick = { PairingPayload.parse(manualText)?.let(onManualPayload) },
                    modifier = Modifier.padding(top = 8.dp),
                    enabled = manualText.isNotBlank()
                ) { Text("Pair (debug)") }
            }
        }
    }
}

@Composable
private fun QrScanner(onScanned: (PairingPayload) -> Unit, onError: (String) -> Unit) {
    val lifecycleOwner = LocalLifecycleOwner.current
    val decoderExecutor = remember { Executors.newSingleThreadExecutor() }
    val handled = remember { AtomicBoolean(false) }
    val disposed = remember { AtomicBoolean(false) }
    val providerRef = remember { AtomicReference<ProcessCameraProvider?>(null) }
    val reader = remember {
        MultiFormatReader().apply {
            setHints(
                mapOf(
                    DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE),
                    DecodeHintType.CHARACTER_SET to "UTF-8",
                    DecodeHintType.TRY_HARDER to true
                )
            )
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            disposed.set(true)
            runCatching { providerRef.getAndSet(null)?.unbindAll() }
            decoderExecutor.shutdownNow()
        }
    }

    AndroidView(
        modifier = Modifier.fillMaxSize(),
        factory = { ctx ->
            val previewView = PreviewView(ctx)
            val cameraProviderFuture = ProcessCameraProvider.getInstance(ctx)

            cameraProviderFuture.addListener({
                if (disposed.get()) return@addListener
                try {
                    val cameraProvider = cameraProviderFuture.get()
                    providerRef.set(cameraProvider)
                    val preview = Preview.Builder().build().also {
                        it.setSurfaceProvider(previewView.surfaceProvider)
                    }
                    val analysis = ImageAnalysis.Builder()
                        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                        .build()

                    analysis.setAnalyzer(decoderExecutor) { imageProxy ->
                        try {
                            if (handled.get() || disposed.get()) return@setAnalyzer
                            val luminance = extractLuminance(imageProxy) ?: return@setAnalyzer
                            val source = PlanarYUVLuminanceSource(
                                luminance,
                                imageProxy.width,
                                imageProxy.height,
                                0,
                                0,
                                imageProxy.width,
                                imageProxy.height,
                                false
                            )
                            val bitmap = BinaryBitmap(HybridBinarizer(source))
                            val raw = try {
                                reader.decodeWithState(bitmap).text
                            } catch (_: Exception) {
                                null
                            } finally {
                                reader.reset()
                            }
                            val payload = raw?.let(PairingPayload::parse) ?: return@setAnalyzer
                            if (handled.compareAndSet(false, true) && !disposed.get()) {
                                ContextCompat.getMainExecutor(ctx).execute {
                                    if (!disposed.get()) onScanned(payload)
                                }
                            }
                        } finally {
                            imageProxy.close()
                        }
                    }

                    cameraProvider.unbindAll()
                    cameraProvider.bindToLifecycle(
                        lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis
                    )
                } catch (e: Exception) {
                    if (handled.compareAndSet(false, true) && !disposed.get()) {
                        onError(
                            "The camera could not be opened for QR pairing (${e.message ?: e.javaClass.simpleName}). " +
                                "You can enter the endpoint, pairing code and certificate fingerprint manually."
                        )
                    }
                }
            }, ContextCompat.getMainExecutor(ctx))

            previewView
        }
    )
}

/**
 * ZXing needs a tightly-packed luminance plane. CameraX YUV_420_888 planes may contain row/pixel
 * padding, so copy only the logical Y samples instead of assuming rowStride == image width.
 */
private fun extractLuminance(image: ImageProxy): ByteArray? {
    if (image.planes.isEmpty() || image.width <= 0 || image.height <= 0) return null
    val plane = image.planes[0]
    val rowStride = plane.rowStride
    val pixelStride = plane.pixelStride
    if (rowStride <= 0 || pixelStride <= 0) return null

    val buffer = plane.buffer.duplicate()
    val output = ByteArray(image.width * image.height)
    for (row in 0 until image.height) {
        val rowStart = row * rowStride
        val outputStart = row * image.width
        for (column in 0 until image.width) {
            val sourceIndex = rowStart + column * pixelStride
            if (sourceIndex < 0 || sourceIndex >= buffer.limit()) return null
            output[outputStart + column] = buffer.get(sourceIndex)
        }
    }
    return output
}
