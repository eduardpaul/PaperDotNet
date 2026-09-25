from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .operation_status import OperationStatus

@dataclass
class OperationResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The completedAt property
    completed_at: Optional[datetime.datetime] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The error property
    error: Optional[str] = None
    # The id property
    id: Optional[UUID] = None
    # The percentComplete property
    percent_complete: Optional[int] = None
    # The startedAt property
    started_at: Optional[datetime.datetime] = None
    # The status property
    status: Optional[OperationStatus] = None
    # The type property
    type: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> OperationResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: OperationResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return OperationResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .operation_status import OperationStatus

        from .operation_status import OperationStatus

        fields: dict[str, Callable[[Any], None]] = {
            "completedAt": lambda n : setattr(self, 'completed_at', n.get_datetime_value()),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "error": lambda n : setattr(self, 'error', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "percentComplete": lambda n : setattr(self, 'percent_complete', n.get_int_value()),
            "startedAt": lambda n : setattr(self, 'started_at', n.get_datetime_value()),
            "status": lambda n : setattr(self, 'status', n.get_enum_value(OperationStatus)),
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
        writer.write_datetime_value("completedAt", self.completed_at)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_str_value("error", self.error)
        writer.write_uuid_value("id", self.id)
        writer.write_int_value("percentComplete", self.percent_complete)
        writer.write_datetime_value("startedAt", self.started_at)
        writer.write_enum_value("status", self.status)
        writer.write_str_value("type", self.type)
        writer.write_additional_data_value(self.additional_data)
    

