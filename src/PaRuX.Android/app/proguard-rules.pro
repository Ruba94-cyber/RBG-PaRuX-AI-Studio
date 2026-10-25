# PaRuX Android release rules. Kept minimal: no reflection-heavy libraries beyond
# kotlinx.serialization and Retrofit, both of which ship their own consumer rules.
-keepattributes *Annotation*
-keepattributes Signature
