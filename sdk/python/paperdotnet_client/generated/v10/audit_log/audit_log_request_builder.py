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
    from ...models.audit_page import AuditPage
    from ...models.http_validation_problem_details import HttpValidationProblemDetails

class AuditLogRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/auditLog
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new AuditLogRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/auditLog{?entityId*,entityType*,from*,to*,userId*}", path_parameters)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[AuditLogRequestBuilderGetQueryParameters]] = None) -> Optional[AuditPage]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[AuditPage]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from ...models.http_validation_problem_details import HttpValidationProblemDetails

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": HttpValidationProblemDetails,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ...models.audit_page import AuditPage

        return await self.request_adapter.send_async(request_info, AuditPage, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[AuditLogRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def with_url(self,raw_url: str) -> AuditLogRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: AuditLogRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return AuditLogRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class AuditLogRequestBuilderGetQueryParameters():
        def get_query_parameter(self,original_name: str) -> str:
            """
            Maps the query parameters names to their encoded names for the URI template parsing.
            param original_name: The original query parameter name in the class.
            Returns: str
            """
            if original_name is None:
                raise TypeError("original_name cannot be null.")
            if original_name == "entity_id":
                return "entityId"
            if original_name == "entity_type":
                return "entityType"
            if original_name == "from_":
                return "from"
            if original_name == "user_id":
                return "userId"
            if original_name == "to":
                return "to"
            return original_name
        
        entity_id: Optional[UUID] = None

        entity_type: Optional[str] = None

        from_: Optional[datetime.datetime] = None

        to: Optional[datetime.datetime] = None

        user_id: Optional[UUID] = None

    
    @dataclass
    class AuditLogRequestBuilderGetRequestConfiguration(RequestConfiguration[AuditLogRequestBuilderGetQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

