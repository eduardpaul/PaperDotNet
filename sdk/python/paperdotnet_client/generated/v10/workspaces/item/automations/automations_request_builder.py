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
    from .....models.api_problem import ApiProblem
    from .....models.automation_request import AutomationRequest
    from .....models.automation_response import AutomationResponse
    from .item.automations_item_request_builder import AutomationsItemRequestBuilder
    from .runs.runs_request_builder import RunsRequestBuilder

class AutomationsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{workspaceId}/automations
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new AutomationsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{workspaceId}/automations", path_parameters)
    
    def by_id(self,id: UUID) -> AutomationsItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.workspaces.item.automations.item collection
        param id: Unique identifier of the item
        Returns: AutomationsItemRequestBuilder
        """
        if id is None:
            raise TypeError("id cannot be null.")
        from .item.automations_item_request_builder import AutomationsItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["id"] = id
        return AutomationsItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[list[AutomationResponse]]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[list[AutomationResponse]]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from .....models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from .....models.automation_response import AutomationResponse

        return await self.request_adapter.send_collection_async(request_info, AutomationResponse, error_mapping)
    
    async def post(self,body: AutomationRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[AutomationResponse]:
        """
        param body: The request body
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[AutomationResponse]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_post_request_information(
            body, request_configuration
        )
        from .....models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": ApiProblem,
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from .....models.automation_response import AutomationResponse

        return await self.request_adapter.send_async(request_info, AutomationResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_post_request_information(self,body: AutomationRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: The request body
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = RequestInformation(Method.POST, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        request_info.set_content_from_parsable(self.request_adapter, "application/json", body)
        return request_info
    
    def with_url(self,raw_url: str) -> AutomationsRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: AutomationsRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return AutomationsRequestBuilder(self.request_adapter, raw_url)
    
    @property
    def runs(self) -> RunsRequestBuilder:
        """
        The runs property
        """
        from .runs.runs_request_builder import RunsRequestBuilder

        return RunsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @dataclass
    class AutomationsRequestBuilderGetRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class AutomationsRequestBuilderPostRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

