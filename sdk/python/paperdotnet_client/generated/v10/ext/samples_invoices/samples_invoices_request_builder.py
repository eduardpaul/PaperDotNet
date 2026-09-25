from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .approvals.approvals_request_builder import ApprovalsRequestBuilder
    from .stats.stats_request_builder import StatsRequestBuilder
    from .workspaces.workspaces_request_builder import WorkspacesRequestBuilder

class SamplesInvoicesRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/ext/samples.invoices
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new SamplesInvoicesRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/ext/samples.invoices", path_parameters)
    
    @property
    def approvals(self) -> ApprovalsRequestBuilder:
        """
        The approvals property
        """
        from .approvals.approvals_request_builder import ApprovalsRequestBuilder

        return ApprovalsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def stats(self) -> StatsRequestBuilder:
        """
        The stats property
        """
        from .stats.stats_request_builder import StatsRequestBuilder

        return StatsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def workspaces(self) -> WorkspacesRequestBuilder:
        """
        The workspaces property
        """
        from .workspaces.workspaces_request_builder import WorkspacesRequestBuilder

        return WorkspacesRequestBuilder(self.request_adapter, self.path_parameters)
    

