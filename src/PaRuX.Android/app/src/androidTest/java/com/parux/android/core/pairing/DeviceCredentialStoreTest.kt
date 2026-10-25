package com.parux.android.core.pairing

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The first instrumented tests this project has had, covering the one thing that genuinely cannot
 * be tested on the JVM: [DeviceCredentialStore] encrypts the paired-device token with an AES-256
 * key generated inside **AndroidKeyStore**, which only exists on a device or emulator. Until now
 * the most security-sensitive class in the Android app - the one holding the credential that grants
 * access to the user's PC - had no automated coverage at all.
 *
 * What is asserted is the property the class exists to provide, not its internals: the token
 * survives a round trip, and the plaintext token never appears in the preference file it writes.
 */
@RunWith(AndroidJUnit4::class)
class DeviceCredentialStoreTest {

    private lateinit var context: Context
    private lateinit var store: DeviceCredentialStore

    private val sample = DeviceCredentialStore.Credential(
        endpoint = "https://192.168.1.50:51124",
        deviceToken = "token-QwertyUiop77-do-not-leak",
        certificateFingerprint = "0A0269B3",
        deviceName = "Test Device"
    )

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        store = DeviceCredentialStore(context)
        store.clear()
    }

    @After
    fun tearDown() {
        store.clear()
    }

    @Test
    fun a_saved_credential_round_trips_intact() {
        store.save(sample)

        val loaded = DeviceCredentialStore(context).load()

        assertNotNull("Credential did not survive a save/load round trip", loaded)
        assertEquals(sample.endpoint, loaded!!.endpoint)
        assertEquals(sample.deviceToken, loaded.deviceToken)
        assertEquals(sample.certificateFingerprint, loaded.certificateFingerprint)
        assertEquals(sample.deviceName, loaded.deviceName)
    }

    @Test
    fun the_stored_blob_never_contains_the_plaintext_token() {
        // The whole point of the Keystore work: an attacker with the preference file - a backup,
        // a rooted device, an adb pull - must not read the token out of it.
        store.save(sample)

        val raw = context.getSharedPreferences(DeviceCredentialStore.PREFS_NAME, Context.MODE_PRIVATE)
            .all
            .values
            .joinToString(" ") { it.toString() }

        assertTrue("Nothing was written to the preference file", raw.isNotEmpty())
        assertFalse("Plaintext token found in the stored blob", raw.contains(sample.deviceToken))
        assertFalse("Plaintext endpoint found in the stored blob", raw.contains(sample.endpoint))
        assertFalse("Plaintext fingerprint found in the stored blob", raw.contains(sample.certificateFingerprint))
    }

    @Test
    fun two_saves_of_the_same_credential_do_not_produce_the_same_ciphertext() {
        // AES-GCM must never reuse an IV. Identical ciphertext for identical plaintext would mean
        // the IV was fixed, which breaks the mode outright.
        store.save(sample)
        val first = readBlob()
        store.save(sample)
        val second = readBlob()

        assertNotNull(first)
        assertNotNull(second)
        assertFalse("The same plaintext encrypted to the same blob twice - the IV is not random", first == second)
    }

    @Test
    fun clearing_removes_the_credential_and_leaves_the_device_unpaired() {
        store.save(sample)
        assertTrue(store.isPaired())

        store.clear()

        assertFalse(store.isPaired())
        assertNull(DeviceCredentialStore(context).load())
    }

    @Test
    fun a_corrupted_blob_is_discarded_rather_than_throwing() {
        // A truncated or tampered blob must leave the app unpaired, not crash it on launch -
        // load() is called during startup routing.
        store.save(sample)
        context.getSharedPreferences(DeviceCredentialStore.PREFS_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString(DeviceCredentialStore.KEY_BLOB,
                DeviceCredentialStore.BLOB_VERSION + ":not-base64:also-not-base64")
            .commit()

        assertNull(DeviceCredentialStore(context).load())
        assertFalse(DeviceCredentialStore(context).isPaired())
    }

    @Test
    fun a_blob_whose_authentication_tag_fails_is_rejected() {
        // GCM authenticates as well as encrypts. Flipping a byte of ciphertext must fail the tag
        // check and be discarded, not decrypt into something the app then trusts.
        store.save(sample)
        val prefs = context.getSharedPreferences(DeviceCredentialStore.PREFS_NAME, Context.MODE_PRIVATE)
        val blob = prefs.getString(DeviceCredentialStore.KEY_BLOB, null)!!
        val parts = blob.split(':', limit = 3)
        val tampered = parts[0] + ":" + parts[1] + ":" + flipFirstCharacter(parts[2])
        prefs.edit().putString(DeviceCredentialStore.KEY_BLOB, tampered).commit()

        assertNull(DeviceCredentialStore(context).load())
    }

    private fun readBlob(): String? = context
        .getSharedPreferences(DeviceCredentialStore.PREFS_NAME, Context.MODE_PRIVATE)
        .getString(DeviceCredentialStore.KEY_BLOB, null)

    private fun flipFirstCharacter(value: String): String {
        val replacement = if (value.first() == 'A') 'B' else 'A'
        return replacement + value.substring(1)
    }
}
