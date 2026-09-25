"""The Python SDK (API-03): the Kiota-generated client plus a factory for authentication and the tenant.

    from paperdotnet_client import create_client
    api = create_client("https://dms.example.com", access_token)
    workspaces = await api.v10.workspaces.get()
"""
from __future__ import annotations

import httpx
from kiota_abstractions.authentication.anonymous_authentication_provider import AnonymousAuthenticationProvider
from kiota_http.httpx_request_adapter import HttpxRequestAdapter

from .generated.paper_dot_net_api_client import PaperDotNetApiClient

__all__ = ["PaperDotNetApiClient", "create_client"]


def create_client(base_url: str, access_token: str, tenant: str | None = None,
                  http_client: httpx.AsyncClient | None = None) -> PaperDotNetApiClient:
    """Creates a client for an installation; `tenant` is needed when the host name does not select it."""
    headers = {"Authorization": f"Bearer {access_token}"}
    if tenant:
        headers["X-Tenant"] = tenant
    client = http_client or httpx.AsyncClient()
    client.headers.update(headers)
    adapter = HttpxRequestAdapter(AnonymousAuthenticationProvider(), http_client=client)
    adapter.base_url = base_url.rstrip("/")
    return PaperDotNetApiClient(adapter)
