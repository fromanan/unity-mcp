"""Bounded, session-scoped storage for oversized MCP tool results."""

from __future__ import annotations

from collections import OrderedDict
from dataclasses import dataclass
import secrets
import time


@dataclass(frozen=True)
class StoredResult:
    content: str
    content_type: str
    owner: str
    tool_name: str
    size_bytes: int
    created_at: float
    expires_at: float


class ResultStore:
    """TTL/LRU result store bounded by entry count, item bytes, and total bytes."""

    def __init__(
        self,
        *,
        max_entries: int = 64,
        max_total_bytes: int = 16 * 1024 * 1024,
        max_item_bytes: int = 4 * 1024 * 1024,
        ttl_seconds: float = 300.0,
    ) -> None:
        self.max_entries = max(1, max_entries)
        self.max_total_bytes = max(1, max_total_bytes)
        self.max_item_bytes = min(max(1, max_item_bytes), self.max_total_bytes)
        self.ttl_seconds = max(1.0, ttl_seconds)
        self._entries: OrderedDict[str, StoredResult] = OrderedDict()
        self._total_bytes = 0

    def put(
        self,
        content: str,
        *,
        content_type: str,
        owner: str,
        tool_name: str,
        size_bytes: int | None = None,
    ) -> str | None:
        encoded_size = (
            len(content.encode("utf-8"))
            if size_bytes is None
            else max(0, int(size_bytes))
        )
        if encoded_size > self.max_item_bytes:
            return None

        now = time.monotonic()
        self._purge_expired(now)
        result_id = secrets.token_urlsafe(18)
        self._entries[result_id] = StoredResult(
            content=content,
            content_type=content_type,
            owner=owner,
            tool_name=tool_name,
            size_bytes=encoded_size,
            created_at=now,
            expires_at=now + self.ttl_seconds,
        )
        self._total_bytes += encoded_size
        self._evict_to_limits()
        return result_id if result_id in self._entries else None

    def get(self, result_id: str, *, owner: str) -> StoredResult | None:
        now = time.monotonic()
        self._purge_expired(now)
        entry = self._entries.get(result_id)
        if entry is None or entry.owner != owner:
            return None
        self._entries.move_to_end(result_id)
        return entry

    def clear(self) -> None:
        self._entries.clear()
        self._total_bytes = 0

    @property
    def entry_count(self) -> int:
        return len(self._entries)

    @property
    def total_bytes(self) -> int:
        return self._total_bytes

    def _purge_expired(self, now: float) -> None:
        expired = [
            result_id
            for result_id, entry in self._entries.items()
            if entry.expires_at <= now
        ]
        for result_id in expired:
            self._remove(result_id)

    def _evict_to_limits(self) -> None:
        while (
            len(self._entries) > self.max_entries
            or self._total_bytes > self.max_total_bytes
        ):
            result_id = next(iter(self._entries))
            self._remove(result_id)

    def _remove(self, result_id: str) -> None:
        entry = self._entries.pop(result_id, None)
        if entry is not None:
            self._total_bytes -= entry.size_bytes


result_store = ResultStore()
