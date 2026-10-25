package com.parux.android.core.pairing

import android.content.Context
import android.util.Base64
import org.json.JSONObject
import java.nio.charset.StandardCharsets
import java.security.GeneralSecurityException
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties

/**
 * Stores only PaRuX pairing credentials and protects them with an AES-256 key generated directly
 * inside AndroidKeyStore. The preference file contains one versioned AES-GCM ciphertext blob plus
 * its random IV; the key is non-exportable and never appears in SharedPreferences or the APK.
 *
 * androidx.security EncryptedSharedPreferences is deliberately not a runtime dependency: no
 * credential is ever written where a plaintext decoder could reach it. Android backup is disabled
 * for the application, so the ciphertext is never restored onto another device.
 */
class DeviceCredentialStore(context: Context) {

    private val appContext = context.applicationContext
    private val prefs = appContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    data class Credential(
        val endpoint: String,
        val deviceToken: String,
        val certificateFingerprint: String,
        val deviceName: String
    )

    fun save(credential: Credential) {
        val plaintext = JSONObject()
            .put(KEY_ENDPOINT, credential.endpoint)
            .put(KEY_TOKEN, credential.deviceToken)
            .put(KEY_FINGERPRINT, credential.certificateFingerprint)
            .put(KEY_DEVICE_NAME, credential.deviceName)
            .toString()
            .toByteArray(StandardCharsets.UTF_8)

        val cipher = Cipher.getInstance(CIPHER_TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        cipher.updateAAD(AAD)
        val ciphertext = cipher.doFinal(plaintext)
        val encoded = buildString {
            append(BLOB_VERSION)
            append(':')
            append(Base64.encodeToString(cipher.iv, Base64.NO_WRAP))
            append(':')
            append(Base64.encodeToString(ciphertext, Base64.NO_WRAP))
        }
        prefs.edit().putString(KEY_BLOB, encoded).apply()
    }

    fun load(): Credential? {
        val encoded = prefs.getString(KEY_BLOB, null) ?: return null
        return try {
            val parts = encoded.split(':', limit = 3)
            if (parts.size != 3 || parts[0] != BLOB_VERSION) return clearCorruptBlob()

            val iv = Base64.decode(parts[1], Base64.NO_WRAP)
            val ciphertext = Base64.decode(parts[2], Base64.NO_WRAP)
            if (iv.size != GCM_IV_BYTES || ciphertext.size <= GCM_TAG_BYTES) return clearCorruptBlob()

            val key = getExistingKey() ?: return clearCorruptBlob()
            val cipher = Cipher.getInstance(CIPHER_TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(GCM_TAG_BITS, iv))
            cipher.updateAAD(AAD)
            val json = JSONObject(String(cipher.doFinal(ciphertext), StandardCharsets.UTF_8))

            val endpoint = json.optString(KEY_ENDPOINT).takeIf { it.isNotBlank() }
                ?: return clearCorruptBlob()
            val token = json.optString(KEY_TOKEN).takeIf { it.isNotBlank() }
                ?: return clearCorruptBlob()
            val fingerprint = json.optString(KEY_FINGERPRINT).takeIf { it.isNotBlank() }
                ?: return clearCorruptBlob()

            Credential(
                endpoint = endpoint,
                deviceToken = token,
                certificateFingerprint = fingerprint,
                deviceName = json.optString(KEY_DEVICE_NAME, "")
            )
        } catch (_: GeneralSecurityException) {
            clearCorruptBlob()
        } catch (_: IllegalArgumentException) {
            clearCorruptBlob()
        } catch (_: RuntimeException) {
            clearCorruptBlob()
        }
    }

    fun isPaired(): Boolean = load() != null

    /**
     * Forget device locally. Deleting the Keystore key as well as the ciphertext makes any stale
     * copy of the old blob cryptographically unrecoverable on this device.
     */
    fun clear() {
        prefs.edit().clear().apply()
        try {
            val keyStore = openKeyStore()
            if (keyStore.containsAlias(KEY_ALIAS)) keyStore.deleteEntry(KEY_ALIAS)
        } catch (_: GeneralSecurityException) {
            // The ciphertext is already gone. A later save will retry normal key creation.
        }
    }

    private fun getExistingKey(): SecretKey? {
        val keyStore = openKeyStore()
        return keyStore.getKey(KEY_ALIAS, null) as? SecretKey
    }

    private fun getOrCreateKey(): SecretKey {
        getExistingKey()?.let { return it }
        val generator = KeyGenerator.getInstance(
            KeyProperties.KEY_ALGORITHM_AES,
            ANDROID_KEYSTORE
        )
        val spec = KeyGenParameterSpec.Builder(
            KEY_ALIAS,
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
        )
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .setRandomizedEncryptionRequired(true)
            .build()
        generator.init(spec)
        return generator.generateKey()
    }

    private fun openKeyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    private fun <T> clearCorruptBlob(): T? {
        prefs.edit().remove(KEY_BLOB).apply()
        return null
    }

    /**
     * Internal rather than private so the instrumented tests can assert against the real
     * preference and key names instead of duplicating them as literals that would silently drift.
     * Still invisible outside this module - nothing about the app's public surface changes.
     */
    internal companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "parux.android.pairing.aes-gcm.v1"
        const val CIPHER_TRANSFORMATION = "AES/GCM/NoPadding"
        const val GCM_TAG_BITS = 128
        const val GCM_TAG_BYTES = GCM_TAG_BITS / 8
        const val GCM_IV_BYTES = 12
        const val BLOB_VERSION = "v1"
        const val PREFS_NAME = "parux_device_credentials_v2"
        const val KEY_BLOB = "credential_blob"
        const val KEY_ENDPOINT = "endpoint"
        const val KEY_TOKEN = "device_token"
        const val KEY_FINGERPRINT = "certificate_fingerprint"
        const val KEY_DEVICE_NAME = "device_name"
        val AAD: ByteArray = "PaRuX.Android.DeviceCredential.v1".toByteArray(StandardCharsets.UTF_8)
    }
}
