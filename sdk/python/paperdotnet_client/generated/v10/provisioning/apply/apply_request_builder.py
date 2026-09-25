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
    from ....models.http_validation_problem_details import HttpValidationProblemDetails
    from ....models.template_result import TemplateResult

class ApplyRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/provisioning/apply
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new ApplyRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/provisioning/apply{?dryRun*,workspaceId*}", path_parameters)
    
    async def post(self,body: bytes, request_configuration: Optional[RequestConfiguration[ApplyRequestBuilderPostQueryParameters]] = None) -> Optional[TemplateResult]:
        """
        param body: Binary request body
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[TemplateResult]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_post_request_information(
            body, request_configuration
        )
        from ....models.http_validation_problem_details import HttpValidationProblemDetails

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": HttpValidationProblemDetails,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ....models.template_result import TemplateResult

        return await self.request_adapter.send_async(request_info, TemplateResult, error_mapping)
    
    def to_post_request_information(self,body: bytes, request_configuration: Optional[RequestConfiguration[ApplyRequestBuilderPostQueryParameters]] = None) -> RequestInformation:
        """
        param body: Binary request body
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = RequestInformation(Method.POST, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        request_info.set_stream_content(body, "application/xml")
        return request_info
    
    def with_url(self,raw_url: str) -> ApplyRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: ApplyRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return ApplyRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class ApplyRequestBuilderPostQueryParameters():
        def get_query_parameter(self,original_name: str) -> str:
            """
            Maps the query parameters names to their encoded names for the URI template parsing.
            param original_name: The original query parameter name in the class.
            Returns: str
            """
            if original_name is None:
                raise TypeError("original_name cannot be null.")
            if original_name == "dry_run":
                return "dryRun"
            if original_name == "workspace_id":
                return "workspaceId"
            return original_name
        
        dry_run: Optional[bool] = None

        workspace_id: Optional[UUID] = None

    
    @dataclass
    class ApplyRequestBuilderPostRequestConfiguration(RequestConfiguration[ApplyRequestBuilderPostQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

