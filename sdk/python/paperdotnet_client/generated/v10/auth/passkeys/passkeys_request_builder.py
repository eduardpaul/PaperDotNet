from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .login.login_request_builder import LoginRequestBuilder
    from .options.options_request_builder import OptionsRequestBuilder

class PasskeysRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/auth/passkeys
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new PasskeysRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/auth/passkeys", path_parameters)
    
    @property
    def login(self) -> LoginRequestBuilder:
        """
        The login property
        """
        from .login.login_request_builder import LoginRequestBuilder

        return LoginRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def options_path(self) -> OptionsRequestBuilder:
        """
        The optionsPath property
        """
        from .options.options_request_builder import OptionsRequestBuilder

        return OptionsRequestBuilder(self.request_adapter, self.path_parameters)
    

