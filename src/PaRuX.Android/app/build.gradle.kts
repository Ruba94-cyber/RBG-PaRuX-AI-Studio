plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

val releaseKeystorePath = System.getenv("PARUX_ANDROID_KEYSTORE")
val releaseStorePassword = System.getenv("PARUX_ANDROID_STORE_PASSWORD")
val releaseKeyPassword = System.getenv("PARUX_ANDROID_KEY_PASSWORD")

// Release publication supplies PARUX_SOURCE_COMMIT after proving tag == clean HEAD. Ordinary
// developer/unsigned release-gate builds deliberately remain possible without Git metadata in the
// APK and advertise that fact as "development" rather than inventing a source revision.
val sourceCommit = System.getenv("PARUX_SOURCE_COMMIT")
    ?.trim()
    ?.lowercase()
    ?.takeIf { Regex("^[0-9a-f]{40}$").matches(it) }
    ?: "development"

android {
    namespace = "com.parux.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.parux.android"
        minSdk = 26
        targetSdk = 35
        versionCode = 16
        versionName = "1.9.6"
        buildConfigField("String", "SOURCE_COMMIT", "\"$sourceCommit\"")

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        if (!releaseKeystorePath.isNullOrBlank()
            && !releaseStorePassword.isNullOrBlank()
            && !releaseKeyPassword.isNullOrBlank()) {
            create("release") {
                storeFile = file(releaseKeystorePath)
                storePassword = releaseStorePassword
                keyAlias = "parux-release"
                keyPassword = releaseKeyPassword
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            signingConfig = signingConfigs.findByName("release")
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
        debug {
            // Distinct application id so a debug install never collides with a future
            // release build on the same device.
            applicationIdSuffix = ".debug"
            isDebuggable = true
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.09.02")
    implementation(composeBom)
    androidTestImplementation(composeBom)

    implementation("androidx.core:core-ktx:1.13.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.4")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.4")
    implementation("androidx.activity:activity-compose:1.9.1")
    implementation("androidx.work:work-runtime-ktx:2.9.1")

    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    debugImplementation("androidx.compose.ui:ui-tooling")

    implementation("androidx.navigation:navigation-compose:2.7.7")

    // Networking: Retrofit + OkHttp, kotlinx.serialization for JSON so the wire types below can
    // mirror PaRuX.Contracts field-for-field. No HTTP logging interceptor is shipped: paired
    // device tokens and local prompts must never be logged by a release network stack.
    implementation("com.squareup.retrofit2:retrofit:2.11.0")
    implementation("com.squareup.retrofit2:converter-kotlinx-serialization:2.11.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.1")

    // QR pairing scan. CameraX owns capture; ZXing Core performs QR decoding fully on-device.
    implementation("androidx.camera:camera-core:1.3.4")
    implementation("androidx.camera:camera-camera2:1.3.4")
    implementation("androidx.camera:camera-lifecycle:1.3.4")
    implementation("androidx.camera:camera-view:1.3.4")
    implementation("com.google.zxing:core:3.5.3")

    // Video playback/streaming for generated video jobs.
    implementation("androidx.media3:media3-exoplayer:1.4.1")
    implementation("androidx.media3:media3-ui:1.4.1")
    implementation("androidx.media3:media3-common:1.4.1")
    implementation("androidx.media3:media3-datasource-okhttp:1.4.1")

    // Image loading for generated/uploaded images (thumbnails, viewer).
    implementation("io.coil-kt:coil-compose:2.6.0")

    // Pairing credentials use the Android platform Keystore API directly (AES-256-GCM).
    // No androidx.security/security-crypto or other wrapper is required.

    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.6.1")
}
