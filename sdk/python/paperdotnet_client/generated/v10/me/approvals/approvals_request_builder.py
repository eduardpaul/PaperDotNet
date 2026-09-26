from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.base_request_configuration import RequestConfiguration
from kiota_abstractions.default_query_parameters import QueryParameters
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.method import Method
from kiota_abstractions.request_adapter import RequestAdapter
from kiota_abstractions.request_information import RequestInformation
from kiota_abstractions.request_option import RequestOption
from kiota_abstractions.serialization import Parsable, ParsableFactory
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID
from warnings import warn

if TYPE_CHECKING:
    from ....models.api_problem import ApiProblem
    from ....models.approval_status import ApprovalStatus
    from ....models.page_of_approval_response import PageOfApprovalResponse
    from .item.approvals_item_request_builder import ApprovalsItemRequestBuilder

class ApprovalsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/me/approvals
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new ApprovalsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/me/approvals{?%24skiptoken*,%24top*,status*}", path_parameters)
    
    def by_id(self,id: UUID) -> ApprovalsItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.me.approvals.item collection
        param id: Unique identifier of the item
        Returns: ApprovalsItemRequestBuilder
        """
        if id is None:
            raise TypeError("id cannot be null.")
        from .item.approvals_item_request_builder import ApprovalsItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["id"] = id
        return ApprovalsItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[ApprovalsRequestBuilderGetQueryParameters]] = None) -> Optional[PageOfApprovalResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[PageOfApprovalResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from ....models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": ApiProblem,
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ....models.page_of_approval_response import PageOfApprovalResponse

        return await self.request_adapter.send_async(request_info, PageOfApprovalResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[ApprovalsRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def with_url(self,raw_url: str) -> ApprovalsRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: ApprovalsRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return ApprovalsRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class ApprovalsRequestBuilderGetQueryParameters():
        def get_query_parameter(self,original_name: str) -> str:
            """
            Maps the query parameters names to their encoded names for the URI template parsing.
            param original_name: The original query parameter name in the class.
            Returns: str
            """
            if original_name is None:
                raise TypeError("original_name cannot be null.")
            if original_name == "skiptoken":
                return "%24skiptoken"
            if original_name == "top":
                return "%24top"
            if original_name == "status":
                return "status"
            return original_name
        
        # Continuation token from @odata.nextLink.
        skiptoken: Optional[str] = None

        status: Optional[ApprovalStatus] = None

        # Page size.
        top: Optional[int] = None

    
    @dataclass
    class ApprovalsRequestBuilderGetRequestConfiguration(RequestConfiguration[ApprovalsRequestBuilderGetQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

