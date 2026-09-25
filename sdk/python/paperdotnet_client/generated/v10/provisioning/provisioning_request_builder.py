from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .apply.apply_request_builder import ApplyRequestBuilder
    from .export.export_request_builder import ExportRequestBuilder
    from .schema.schema_request_builder import SchemaRequestBuilder

class ProvisioningRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/provisioning
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new ProvisioningRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/provisioning", path_parameters)
    
    @property
    def apply(self) -> ApplyRequestBuilder:
        """
        The apply property
        """
        from .apply.apply_request_builder import ApplyRequestBuilder

        return ApplyRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def export(self) -> ExportRequestBuilder:
        """
        The export property
        """
        from .export.export_request_builder import ExportRequestBuilder

        return ExportRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def schema(self) -> SchemaRequestBuilder:
        """
        The schema property
        """
        from .schema.schema_request_builder import SchemaRequestBuilder

        return SchemaRequestBuilder(self.request_adapter, self.path_parameters)
    

