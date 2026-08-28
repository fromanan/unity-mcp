"""API key validation service for remote-hosted mode."""

from __future__ import annotations

import asyncio
from collections import OrderedDict
from dataclasses import dataclass
from hashlib import sha256
import logging
import time
from typing import Any

import httpx

from core.capacity import AUTH_MAX_CONCURRENCY, AUTH_MAX_INFLIGHT

logger = logging.getLogger("mcp-for-unity-server")


@dataclass
class ValidationResult:
    """Result of an API key validation."""

    valid: bool
    user_id: str | None = None
    metadata: dict[str, Any] | None = None
    error: str | None = None
    cacheable: bool = True


class ApiKeyService:
    """Validate API keys with bounded LRU caching and per-key singleflight."""

    _instance: "ApiKeyService | None" = None

    REQUEST_TIMEOUT: float = 5.0
    MAX_RETRIES: int = 1

    def __init__(
        self,
        validation_url: str,
        cache_ttl: float = 300.0,
        cache_max_entries: int = 1024,
        service_token_header: str | None = None,
        service_token: str | None = None,
    ):
        self._validation_url = validation_url
        self._cache_ttl = cache_ttl
        self._cache_max_entries = max(1, cache_max_entries)
        self._service_token_header = service_token_header
        self._service_token = service_token
        # Raw credentials are never retained in caches or singleflight maps.
        self._cache: OrderedDict[str, tuple[
            bool, str | None, dict[str, Any] | None, float
        ]] = OrderedDict()
        self._cache_lock = asyncio.Lock()
        self._inflight: dict[str, asyncio.Task[ValidationResult]] = {}
        self._max_inflight = AUTH_MAX_INFLIGHT
        self._validation_semaphore = asyncio.Semaphore(AUTH_MAX_CONCURRENCY)
        self._next_expiry = float("inf")
        self._client: httpx.AsyncClient | None = None
        self._client_lock = asyncio.Lock()
        ApiKeyService._instance = self

    @classmethod
    def get_instance(cls) -> "ApiKeyService":
        if cls._instance is None:
            raise RuntimeError("ApiKeyService not initialized")
        return cls._instance

    @classmethod
    def is_initialized(cls) -> bool:
        return cls._instance is not None

    @classmethod
    async def close_instance(cls) -> None:
        instance = cls._instance
        if instance is not None:
            await instance.close()
            if cls._instance is instance:
                cls._instance = None

    async def validate(self, api_key: str) -> ValidationResult:
        if not api_key:
            return ValidationResult(valid=False, error="API key required")

        cache_key = self._cache_key(api_key)
        now = time.monotonic()
        async with self._cache_lock:
            if now >= self._next_expiry:
                self._purge_expired_locked(now)
            cached = self._cache.get(cache_key)
            if cached is not None and cached[3] <= now:
                self._cache.pop(cache_key, None)
                self._recalculate_next_expiry_locked()
                cached = None
            if cached is not None:
                valid, user_id, metadata, _ = cached
                self._cache.move_to_end(cache_key)
                if valid:
                    return ValidationResult(
                        valid=True,
                        user_id=user_id,
                        metadata=metadata,
                    )
                return ValidationResult(valid=False, error="Invalid API key")

            task = self._inflight.get(cache_key)
            if task is None:
                if len(self._inflight) >= self._max_inflight:
                    return ValidationResult(
                        valid=False,
                        error="Auth service unavailable (validation capacity reached)",
                        cacheable=False,
                    )
                task = asyncio.create_task(
                    self._validate_and_cache(api_key, cache_key),
                    name="api-key-validation",
                )
                self._inflight[cache_key] = task

        # One cancelled requester must not cancel validation for all waiters.
        return await asyncio.shield(task)

    async def _validate_and_cache(
        self,
        api_key: str,
        cache_key: str,
    ) -> ValidationResult:
        current_task = asyncio.current_task()
        try:
            async with self._validation_semaphore:
                result = await self._validate_external(api_key)
            if not result.cacheable:
                return result

            async with self._cache_lock:
                now = time.monotonic()
                expires_at = now + self._cache_ttl
                if now >= self._next_expiry:
                    self._purge_expired_locked(now)
                self._cache[cache_key] = (
                    result.valid,
                    result.user_id,
                    result.metadata,
                    expires_at,
                )
                self._cache.move_to_end(cache_key)
                self._next_expiry = min(self._next_expiry, expires_at)
                while len(self._cache) > self._cache_max_entries:
                    self._cache.popitem(last=False)
                self._recalculate_next_expiry_locked()
            return result
        finally:
            # Clean up even when every requester was cancelled before this
            # shared validation completed.
            async with self._cache_lock:
                if self._inflight.get(cache_key) is current_task:
                    self._inflight.pop(cache_key, None)

    async def _get_client(self) -> httpx.AsyncClient:
        if self._client is not None:
            return self._client
        async with self._client_lock:
            if self._client is None:
                self._client = httpx.AsyncClient(timeout=self.REQUEST_TIMEOUT)
            return self._client

    async def _validate_external(self, api_key: str) -> ValidationResult:
        """Call the external validation endpoint and fail closed on errors."""
        headers = {"Content-Type": "application/json"}
        if self._service_token_header and self._service_token:
            headers[self._service_token_header] = self._service_token

        for attempt in range(self.MAX_RETRIES + 1):
            try:
                client = await self._get_client()
                response = await client.post(
                    self._validation_url,
                    json={"api_key": api_key},
                    headers=headers,
                )

                if response.status_code == 200:
                    data = response.json()
                    if data.get("valid"):
                        return ValidationResult(
                            valid=True,
                            user_id=data.get("user_id"),
                            metadata=data.get("metadata"),
                        )
                    return ValidationResult(
                        valid=False,
                        error=data.get("error", "Invalid API key"),
                    )
                if response.status_code == 401:
                    return ValidationResult(valid=False, error="Invalid API key")

                logger.warning(
                    "API key validation returned status %d",
                    response.status_code,
                )
                return ValidationResult(
                    valid=False,
                    error=f"Auth service error (status {response.status_code})",
                    cacheable=False,
                )
            except httpx.TimeoutException:
                if attempt < self.MAX_RETRIES:
                    logger.debug(
                        "API key validation timed out; retrying...",
                    )
                    await asyncio.sleep(0.1 * (attempt + 1))
                    continue
                logger.warning(
                    "API key validation timed out after %d attempts",
                    attempt + 1,
                )
                return ValidationResult(
                    valid=False,
                    error="Auth service timeout",
                    cacheable=False,
                )
            except httpx.RequestError as exc:
                if attempt < self.MAX_RETRIES:
                    logger.debug(
                        "API key validation request error (%s); retrying...",
                        exc,
                    )
                    await asyncio.sleep(0.1 * (attempt + 1))
                    continue
                logger.warning(
                    "API key validation request error: %s",
                    exc,
                )
                return ValidationResult(
                    valid=False,
                    error="Auth service unavailable",
                    cacheable=False,
                )
            except Exception as exc:
                logger.error(
                    "Unexpected API key validation error: %s",
                    exc,
                )
                return ValidationResult(
                    valid=False,
                    error="Auth service error",
                    cacheable=False,
                )

        return ValidationResult(
            valid=False,
            error="Auth service error",
            cacheable=False,
        )

    async def invalidate_cache(self, api_key: str) -> None:
        async with self._cache_lock:
            self._cache.pop(self._cache_key(api_key), None)
            self._recalculate_next_expiry_locked()

    async def clear_cache(self) -> None:
        async with self._cache_lock:
            self._cache.clear()
            self._next_expiry = float("inf")

    async def close(self) -> None:
        async with self._cache_lock:
            tasks = list(self._inflight.values())
            self._inflight.clear()
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)

        async with self._client_lock:
            if self._client is not None:
                await self._client.aclose()
                self._client = None

    @staticmethod
    def _cache_key(api_key: str) -> str:
        return sha256(api_key.encode("utf-8")).hexdigest()

    def _purge_expired_locked(self, now: float) -> None:
        expired = [
            key
            for key, (_, _, _, expires_at) in self._cache.items()
            if expires_at <= now
        ]
        for key in expired:
            self._cache.pop(key, None)
        self._recalculate_next_expiry_locked()

    def _recalculate_next_expiry_locked(self) -> None:
        self._next_expiry = min(
            (entry[3] for entry in self._cache.values()),
            default=float("inf"),
        )


__all__ = ["ApiKeyService", "ValidationResult"]
