from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.api_error import APIError
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .api_problem_errors import ApiProblem_errors

@dataclass
class ApiProblem(APIError, AdditionalDataHolder, Parsable):
    """
    The problem body of every error response (RFC 9457 with PaperDotNet's `code` and validation `errors`);documented once so SDKs throw one typed error.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The code property
    code: Optional[str] = None
    # The detail property
    detail: Optional[str] = None
    # The errors property
    errors: Optional[ApiProblem_errors] = None
    # The instance property
    instance: Optional[str] = None
    # The status property
    status: Optional[int] = None
    # The title property
    title: Optional[str] = None
    # The traceId property
    trace_id: Optional[str] = None
    # The type property
    type: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ApiProblem:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ApiProblem
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ApiProblem()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .api_problem_errors import ApiProblem_errors

        from .api_problem_errors import ApiProblem_errors

        fields: dict[str, Callable[[Any], None]] = {
            "code": lambda n : setattr(self, 'code', n.get_str_value()),
            "detail": lambda n : setattr(self, 'detail', n.get_str_value()),
            "errors": lambda n : setattr(self, 'errors', n.get_object_value(ApiProblem_errors)),
            "instance": lambda n : setattr(self, 'instance', n.get_str_value()),
            "status": lambda n : setattr(self, 'status', n.get_int_value()),
            "title": lambda n : setattr(self, 'title', n.get_str_value()),
            "traceId": lambda n : setattr(self, 'trace_id', n.get_str_value()),
            "type": lambda n : setattr(self, 'type', n.get_str_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_str_value("code", self.code)
        writer.write_str_value("detail", self.detail)
        writer.write_object_value("errors", self.errors)
        writer.write_str_value("instance", self.instance)
        writer.write_int_value("status", self.status)
        writer.write_str_value("title", self.title)
        writer.write_str_value("traceId", self.trace_id)
        writer.write_str_value("type", self.type)
        writer.write_additional_data_value(self.additional_data)
    
    @property
    def primary_message(self) -> Optional[str]:
        """
        The primary error message.
        """
        return super().message

