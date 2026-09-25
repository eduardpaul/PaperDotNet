from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .run_status import RunStatus

@dataclass
class RuleRunResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The completedAt property
    completed_at: Optional[datetime.datetime] = None
    # The error property
    error: Optional[str] = None
    # The eventId property
    event_id: Optional[UUID] = None
    # The id property
    id: Optional[UUID] = None
    # The itemId property
    item_id: Optional[UUID] = None
    # The startedAt property
    started_at: Optional[datetime.datetime] = None
    # The status property
    status: Optional[RunStatus] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> RuleRunResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: RuleRunResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return RuleRunResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .run_status import RunStatus

        from .run_status import RunStatus

        fields: dict[str, Callable[[Any], None]] = {
            "completedAt": lambda n : setattr(self, 'completed_at', n.get_datetime_value()),
            "error": lambda n : setattr(self, 'error', n.get_str_value()),
            "eventId": lambda n : setattr(self, 'event_id', n.get_uuid_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "itemId": lambda n : setattr(self, 'item_id', n.get_uuid_value()),
            "startedAt": lambda n : setattr(self, 'started_at', n.get_datetime_value()),
            "status": lambda n : setattr(self, 'status', n.get_enum_value(RunStatus)),
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
        writer.write_str_value("error", self.error)
        writer.write_uuid_value("eventId", self.event_id)
        writer.write_uuid_value("id", self.id)
        writer.write_uuid_value("itemId", self.item_id)
        writer.write_datetime_value("startedAt", self.started_at)
        writer.write_enum_value("status", self.status)
        writer.write_additional_data_value(self.additional_data)
    

