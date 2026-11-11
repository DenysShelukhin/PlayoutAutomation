# Local absolute playout: foreground and background safety

AIR-02 uses original imported UTC anchors derived from Europe/Kyiv XML times.
Ready starts do not depend on the readiness of the entire rundown. Missing/review
slots retain a diagnostic image until the next original anchor.

The immediate next Ready producer may be preloaded. Native diagnostic loop PLAY
must not consume that foreign background producer: doing so could put the next
programme on air early. While an absolute diagnostic loop is already foreground
and a different item is prepared, its ordinary LoadNext/Play loop commands are
suppressed. The current diagnostic image may hold at EOF until the real anchor.
Other channels, manual/nonabsolute operations and ordinary looping media retain
their existing behavior. An absolute replay cannot accidentally promote another
background item merely because its current foreground reference matches.

Bounded VP26O references belong to a preparation root, whose manual sibling order
is not an editorial successor. Their successor is selected by the absolute clock.

Legacy producer startup may lag behind the native engine's counter. The plugin
therefore reads only the actual foreground producer asynchronously over local AMCP.
Background producers and another file/revision do not supply position evidence.
The path must match the current verified C-cache entry. Slow queries, stale samples,
another active event, unavailable media and out-of-file frame numbers cannot seek.

When the physical position differs by more than12 output frames, an existing
producer CALL SEEK requests the current original-anchor frame. The seek does not
reload the rundown, change event dates or replace media/GUIDs. Correction has a5s
cooldown and is suppressed in the final second of a slot. Next-event boundaries
remain independently anchored, so delay is not accumulated. Physical samples and
actual corrections are written privately to separate JSON/JSONL receipts.

This is not a frame-exact/gapless claim. Validate foreground identity before and
after boundaries, producer frame position, and actual NDI/post-vMix audio/video.
An as-run UI counter or SDK dropped-frame0 alone is insufficient acceptance.
