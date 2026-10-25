package com.parux.android.ui.theme

import android.app.Activity
import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

// Same brand tokens as src/PaRuX.App/Styles/Theme.xaml, so Windows and Android read as one product.
val PaRuXBrand = Color(0xFF6C5CE7)
val PaRuXBrandLight = Color(0xFF8B7BFF)
val PaRuXBrandDeep = Color(0xFF4C3FBF)
val PaRuXSecondary = Color(0xFF0FA9C7)
val PaRuXDanger = Color(0xFFB02A1D)
val PaRuXSuccess = Color(0xFF0F7B3C)
val PaRuXWarn = Color(0xFFB8860B)

private val LightColors = lightColorScheme(
    primary = PaRuXBrand,
    onPrimary = Color.White,
    secondary = PaRuXSecondary,
    surface = Color(0xFFFBFAFD),
    background = Color(0xFFFBFAFD),
    error = PaRuXDanger
)

private val DarkColors = darkColorScheme(
    primary = PaRuXBrandLight,
    onPrimary = Color(0xFF241E52),
    secondary = Color(0xFF22D3EE),
    surface = Color(0xFF16151C),
    background = Color(0xFF16151C),
    error = Color(0xFFFF9A8F)
)

@Composable
fun PaRuXTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    // Dynamic (Material You) color is available from Android 12, but PaRuX keeps its own
    // brand identity by default rather than re-skinning to the wallpaper - off unless asked.
    dynamicColor: Boolean = false,
    content: @Composable () -> Unit
) {
    val context = LocalContext.current
    val colorScheme = when {
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ->
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        darkTheme -> DarkColors
        else -> LightColors
    }

    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as Activity).window
            window.statusBarColor = colorScheme.primary.toArgb()
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = !darkTheme
        }
    }

    MaterialTheme(
        colorScheme = colorScheme,
        content = content
    )
}
