from __future__ import annotations
import datetime
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
    from ...models.api_problem import ApiProblem
    from ...models.search_response import SearchResponse
    from .reindex.reindex_request_builder import ReindexRequestBuilder

class SearchRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/search
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new SearchRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/search{?%24skip*,%24top*,containerId*,contentTypeId*,createdBy*,mode*,q*,termId*,updatedFrom*,updatedTo*,workspaceId*}", path_parameters)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[SearchRequestBuilderGetQueryParameters]] = None) -> Optional[SearchResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[SearchResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from ...models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": ApiProblem,
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ...models.search_response import SearchResponse

        return await self.request_adapter.send_async(request_info, SearchResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[SearchRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def with_url(self,raw_url: str) -> SearchRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: SearchRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return SearchRequestBuilder(self.request_adapter, raw_url)
    
    @property
    def reindex(self) -> ReindexRequestBuilder:
        """
        The reindex property
        """
        from .reindex.reindex_request_builder import ReindexRequestBuilder

        return ReindexRequestBuilder(self.request_adapter, self.path_parameters)
    
    @dataclass
    class SearchRequestBuilderGetQueryParameters():
        def get_query_parameter(self,original_name: str) -> str:
            """
            Maps the query parameters names to their encoded names for the URI template parsing.
            param original_name: The original query parameter name in the class.
            Returns: str
            """
            if original_name is None:
                raise TypeError("original_name cannot be null.")
            if original_name == "container_id":
                return "containerId"
            if original_name == "content_type_id":
                return "contentTypeId"
            if original_name == "created_by":
                return "createdBy"
            if original_name == "skip":
                return "%24skip"
            if original_name == "term_id":
                return "termId"
            if original_name == "top":
                return "%24top"
            if original_name == "updated_from":
                return "updatedFrom"
            if original_name == "updated_to":
                return "updatedTo"
            if original_name == "workspace_id":
                return "workspaceId"
            if original_name == "mode":
                return "mode"
            if original_name == "q":
                return "q"
            return original_name
        
        container_id: Optional[UUID] = None

        content_type_id: Optional[UUID] = None

        created_by: Optional[UUID] = None

        mode: Optional[str] = None

        q: Optional[str] = None

        # Results to skip (use @odata.nextLink to page).
        skip: Optional[int] = None

        term_id: Optional[UUID] = None

        # Page size.
        top: Optional[int] = None

        updated_from: Optional[datetime.datetime] = None

        updated_to: Optional[datetime.datetime] = None

        workspace_id: Optional[UUID] = None

    
    @dataclass
    class SearchRequestBuilderGetRequestConfiguration(RequestConfiguration[SearchRequestBuilderGetQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

