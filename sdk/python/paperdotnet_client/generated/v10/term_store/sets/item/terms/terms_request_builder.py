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
    from ......models.api_problem import ApiProblem
    from ......models.create_term_request import CreateTermRequest
    from ......models.page_of_term_response import PageOfTermResponse
    from ......models.term_response import TermResponse
    from .item.with_term_item_request_builder import WithTermItemRequestBuilder

class TermsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/termStore/sets/{setId}/terms
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new TermsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/termStore/sets/{setId}/terms{?%24skiptoken*,%24top*,includeDeprecated*,parentId*,search*}", path_parameters)
    
    def by_term_id(self,term_id: UUID) -> WithTermItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.termStore.sets.item.terms.item collection
        param term_id: Unique identifier of the item
        Returns: WithTermItemRequestBuilder
        """
        if term_id is None:
            raise TypeError("term_id cannot be null.")
        from .item.with_term_item_request_builder import WithTermItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["termId"] = term_id
        return WithTermItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[TermsRequestBuilderGetQueryParameters]] = None) -> Optional[PageOfTermResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[PageOfTermResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from ......models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ......models.page_of_term_response import PageOfTermResponse

        return await self.request_adapter.send_async(request_info, PageOfTermResponse, error_mapping)
    
    async def post(self,body: CreateTermRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[TermResponse]:
        """
        param body: The request body
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[TermResponse]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_post_request_information(
            body, request_configuration
        )
        from ......models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": ApiProblem,
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ......models.term_response import TermResponse

        return await self.request_adapter.send_async(request_info, TermResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[TermsRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_post_request_information(self,body: CreateTermRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
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
    
    def with_url(self,raw_url: str) -> TermsRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: TermsRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return TermsRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class TermsRequestBuilderGetQueryParameters():
        def get_query_parameter(self,original_name: str) -> str:
            """
            Maps the query parameters names to their encoded names for the URI template parsing.
            param original_name: The original query parameter name in the class.
            Returns: str
            """
            if original_name is None:
                raise TypeError("original_name cannot be null.")
            if original_name == "include_deprecated":
                return "includeDeprecated"
            if original_name == "parent_id":
                return "parentId"
            if original_name == "skiptoken":
                return "%24skiptoken"
            if original_name == "top":
                return "%24top"
            if original_name == "search":
                return "search"
            return original_name
        
        include_deprecated: Optional[bool] = None

        parent_id: Optional[UUID] = None

        search: Optional[str] = None

        # Continuation token from @odata.nextLink.
        skiptoken: Optional[str] = None

        # Page size.
        top: Optional[int] = None

    
    @dataclass
    class TermsRequestBuilderGetRequestConfiguration(RequestConfiguration[TermsRequestBuilderGetQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class TermsRequestBuilderPostRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

