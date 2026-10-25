package com.parux.android.ui.media

import android.content.ContentValues
import android.content.Context
import android.os.Build
import android.provider.MediaStore
import android.widget.Toast
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.FileProvider
import androidx.media3.common.MediaItem
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import coil.compose.AsyncImage
import coil.request.ImageRequest
import com.parux.android.core.AppContainer
import com.parux.android.core.network.MediaJobStatus
import com.parux.android.ui.jobs.JobsViewModel
import java.io.File
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.Request

private const val MAX_MEDIA_DOWNLOAD_BYTES = 512L * 1024L * 1024L

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MediaScreen(viewModel: JobsViewModel, container: AppContainer) {
    val state by viewModel.state.collectAsState()
    val context = LocalContext.current
    LaunchedEffect(Unit) { viewModel.startPolling(context) }

    val completed = state.jobs.filter { it.status == "completed" && it.asset != null }
    var viewing by remember { mutableStateOf<MediaJobStatus?>(null) }

    Scaffold(topBar = { TopAppBar(title = { Text("Media") }) }) { padding ->
        if (completed.isEmpty()) {
            Column(
                Modifier.padding(padding).fillMaxSize().padding(32.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                Text("Nothing generated yet", style = MaterialTheme.typography.titleMedium)
                Text(
                    "Images and videos you generate or receive show up here.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        } else {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(minSize = 120.dp),
                modifier = Modifier.padding(padding).fillMaxSize(),
                contentPadding = androidx.compose.foundation.layout.PaddingValues(8.dp)
            ) {
                items(completed, key = { it.id }) { job ->
                    MediaTile(job, container) { viewing = job }
                }
            }
        }
    }

    viewing?.let { job -> MediaViewerDialog(job = job, container = container, onDismiss = { viewing = null }) }
}

@Composable
private fun MediaTile(job: MediaJobStatus, container: AppContainer, onClick: () -> Unit) {
    val client by container.client.collectAsState()
    val endpoint = client?.endpoint ?: return
    Card(onClick = onClick, modifier = Modifier.padding(4.dp).aspectRatio(1f)) {
        Box(Modifier.fillMaxSize()) {
            if (job.type == "image") {
                AuthenticatedAsyncImage(url = "$endpoint/api/v1/media/${job.id}", container = container)
            } else {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Icon(Icons.Default.PlayArrow, contentDescription = "Video", modifier = Modifier.padding(24.dp))
                }
            }
        }
    }
}

@Composable
private fun AuthenticatedAsyncImage(url: String, container: AppContainer) {
    val context = LocalContext.current
    val client by container.client.collectAsState()
    val okHttp = client?.okHttp
    val imageLoader = remember(okHttp) {
        coil.ImageLoader.Builder(context)
            .okHttpClient { okHttp ?: okhttp3.OkHttpClient() }
            .build()
    }
    AsyncImage(
        model = ImageRequest.Builder(context).data(url).crossfade(true).build(),
        imageLoader = imageLoader,
        contentDescription = null,
        contentScale = ContentScale.Crop,
        modifier = Modifier.fillMaxSize()
    )
}

@Composable
private fun MediaViewerDialog(job: MediaJobStatus, container: AppContainer, onDismiss: () -> Unit) {
    val context = LocalContext.current
    val client by container.client.collectAsState()
    val endpoint = client?.endpoint ?: return
    val okHttp = client?.okHttp ?: return
    val mediaUrl = "$endpoint/api/v1/media/${job.id}"
    val scope = rememberCoroutineScope()

    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss) {
        Box(Modifier.fillMaxSize()) {
            if (job.type == "video") VideoPlayer(url = mediaUrl, okHttp = okHttp)
            else AuthenticatedAsyncImage(url = mediaUrl, container = container)

            Column(Modifier.align(Alignment.TopEnd).padding(8.dp)) {
                IconButton(onClick = onDismiss) { Icon(Icons.Default.Close, contentDescription = "Close") }
                IconButton(onClick = { scope.launch { saveToGallery(context, job, container) } }) {
                    Icon(Icons.Default.Download, contentDescription = "Save")
                }
                IconButton(onClick = { scope.launch { shareMedia(context, job, container) } }) {
                    Icon(Icons.Default.Share, contentDescription = "Share")
                }
            }
        }
    }
}

@Composable
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
private fun VideoPlayer(url: String, okHttp: okhttp3.OkHttpClient) {
    val context = LocalContext.current
    val exoPlayer = remember {
        val dataSourceFactory = OkHttpDataSource.Factory(okHttp)
        ExoPlayer.Builder(context)
            .setMediaSourceFactory(DefaultMediaSourceFactory(dataSourceFactory))
            .build()
            .apply {
                setMediaItem(MediaItem.fromUri(url))
                prepare()
                playWhenReady = true
            }
    }
    DisposableEffect(Unit) { onDispose { exoPlayer.release() } }
    AndroidView(factory = { PlayerView(it).apply { player = exoPlayer } }, modifier = Modifier.fillMaxSize())
}

private suspend fun saveToGallery(context: Context, job: MediaJobStatus, container: AppContainer) {
    val asset = job.asset ?: return
    try {
        withContext(Dispatchers.IO) {
            val client = container.client.value ?: error("PaRuX is not connected.")
            val request = Request.Builder()
                .url("${client.endpoint.trimEnd('/')}/api/v1/media/${job.id}")
                .get()
                .build()
            client.okHttp.newCall(request).execute().use { response ->
                if (!response.isSuccessful) error("PaRuX returned HTTP ${response.code}.")
                val body = response.body ?: error("PaRuX returned an empty media response.")
                rejectOversizedDownload(body.contentLength())
                val isVideo = job.type == "video"
                val mediaType = response.header("Content-Type")?.substringBefore(';')
                    ?.takeIf { it.isNotBlank() } ?: asset.mediaType
                val values = ContentValues().apply {
                    put(MediaStore.MediaColumns.DISPLAY_NAME, safeMediaName(asset.fileName, job.id))
                    put(MediaStore.MediaColumns.MIME_TYPE, mediaType)
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                        put(MediaStore.MediaColumns.RELATIVE_PATH, if (isVideo) "Movies/PaRuX" else "Pictures/PaRuX")
                    }
                }
                val collection = if (isVideo) MediaStore.Video.Media.EXTERNAL_CONTENT_URI
                    else MediaStore.Images.Media.EXTERNAL_CONTENT_URI
                val uri = context.contentResolver.insert(collection, values)
                    ?: error("Android could not create the media destination.")
                try {
                    context.contentResolver.openOutputStream(uri)?.use { output ->
                        body.byteStream().use { input -> copyBounded(input, output) }
                    } ?: error("Android could not open the media destination.")
                } catch (e: Exception) {
                    try { context.contentResolver.delete(uri, null, null) } catch (_: Exception) { }
                    throw e
                }
            }
        }
        Toast.makeText(
            context,
            "Saved to ${if (job.type == "video") "Movies/PaRuX" else "Pictures/PaRuX"}",
            Toast.LENGTH_SHORT
        ).show()
    } catch (e: Exception) {
        Toast.makeText(context, "Couldn't save: ${e.message}", Toast.LENGTH_SHORT).show()
    }
}

private suspend fun shareMedia(context: Context, job: MediaJobStatus, container: AppContainer) {
    val asset = job.asset ?: return
    try {
        val staged = withContext(Dispatchers.IO) {
            val client = container.client.value ?: error("PaRuX is not connected.")
            val shareRoot = File(context.cacheDir, "shared-media").apply { mkdirs() }
            val cacheFile = File(shareRoot, safeMediaName(asset.fileName, job.id))
            val request = Request.Builder()
                .url("${client.endpoint.trimEnd('/')}/api/v1/media/${job.id}")
                .get()
                .build()
            client.okHttp.newCall(request).execute().use { response ->
                if (!response.isSuccessful) error("PaRuX returned HTTP ${response.code}.")
                val body = response.body ?: error("PaRuX returned an empty media response.")
                rejectOversizedDownload(body.contentLength())
                cacheFile.outputStream().use { output ->
                    body.byteStream().use { input -> copyBounded(input, output) }
                }
                val mediaType = response.header("Content-Type")?.substringBefore(';')
                    ?.takeIf { it.isNotBlank() } ?: asset.mediaType
                cacheFile to mediaType
            }
        }

        val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", staged.first)
        val intent = android.content.Intent(android.content.Intent.ACTION_SEND).apply {
            type = staged.second
            putExtra(android.content.Intent.EXTRA_STREAM, uri)
            addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        context.startActivity(android.content.Intent.createChooser(intent, "Share via"))
    } catch (e: Exception) {
        Toast.makeText(context, "Couldn't share: ${e.message}", Toast.LENGTH_SHORT).show()
    }
}

private fun safeMediaName(fileName: String, jobId: String): String =
    File(fileName.ifBlank { "parux-$jobId" }).name
        .takeIf { it.isNotBlank() && it !in setOf(".", "..") }
        ?: "parux-$jobId"

private fun rejectOversizedDownload(contentLength: Long) {
    if (contentLength > MAX_MEDIA_DOWNLOAD_BYTES) {
        error("Media is larger than the Android ${MAX_MEDIA_DOWNLOAD_BYTES / (1024 * 1024)} MB safety limit.")
    }
}

private fun copyBounded(input: java.io.InputStream, output: java.io.OutputStream) {
    val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
    var total = 0L
    while (true) {
        val read = input.read(buffer)
        if (read < 0) return
        total += read
        if (total > MAX_MEDIA_DOWNLOAD_BYTES) error("Media exceeded the Android download safety limit.")
        output.write(buffer, 0, read)
    }
}
