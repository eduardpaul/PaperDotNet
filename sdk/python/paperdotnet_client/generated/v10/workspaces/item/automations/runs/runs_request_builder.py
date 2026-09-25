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
    from ......models.page_of_run_response import PageOfRunResponse
    from ......models.run_status import RunStatus
    from .item.runs_item_request_builder import RunsItemRequestBuilder

class RunsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{-id}/automations/runs
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new RunsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{%2Did}/automations/runs{?automationId*,itemId*,status*}", path_parameters)
    
    def by_id(self,id: UUID) -> RunsItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.workspaces.item.automations.runs.item collection
        param id: Unique identifier of the item
        Returns: RunsItemRequestBuilder
        """
        if id is None:
            raise TypeError("id cannot be null.")
        from .item.runs_item_request_builder import RunsItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["id"] = id
        return RunsItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[RunsRequestBuilderGetQueryParameters]] = None) -> Optional[PageOfRunResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[PageOfRunResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ......models.page_of_run_response import PageOfRunResponse

        return await self.request_adapter.send_async(request_info, PageOfRunResponse, None)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[RunsRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def with_url(self,raw_url: str) -> RunsRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: RunsRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return RunsRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class RunsRequestBuilderGetQueryParameters():
        def get_query_parameter(self,original_name: str) -> str:
            """
            Maps the query parameters names to their encoded names for the URI template parsing.
            param original_name: The original query parameter name in the class.
            Returns: str
            """
            if original_name is None:
                raise TypeError("original_name cannot be null.")
            if original_name == "automation_id":
                return "automationId"
            if original_name == "item_id":
                return "itemId"
            if original_name == "status":
                return "status"
            return original_name
        
        automation_id: Optional[UUID] = None

        item_id: Optional[UUID] = None

        status: Optional[RunStatus] = None

    
    @dataclass
    class RunsRequestBuilderGetRequestConfiguration(RequestConfiguration[RunsRequestBuilderGetQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

