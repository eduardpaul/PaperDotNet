from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .audit_action import AuditAction

@dataclass
class AuditEntryResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The action property
    action: Optional[AuditAction] = None
    # The at property
    at: Optional[datetime.datetime] = None
    # The entityId property
    entity_id: Optional[UUID] = None
    # The entityType property
    entity_type: Optional[str] = None
    # The id property
    id: Optional[UUID] = None
    # The properties property
    properties: Optional[list[str]] = None
    # The traceId property
    trace_id: Optional[str] = None
    # The userId property
    user_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> AuditEntryResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: AuditEntryResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return AuditEntryResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .audit_action import AuditAction

        from .audit_action import AuditAction

        fields: dict[str, Callable[[Any], None]] = {
            "action": lambda n : setattr(self, 'action', n.get_enum_value(AuditAction)),
            "at": lambda n : setattr(self, 'at', n.get_datetime_value()),
            "entityId": lambda n : setattr(self, 'entity_id', n.get_uuid_value()),
            "entityType": lambda n : setattr(self, 'entity_type', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "properties": lambda n : setattr(self, 'properties', n.get_collection_of_primitive_values(str)),
            "traceId": lambda n : setattr(self, 'trace_id', n.get_str_value()),
            "userId": lambda n : setattr(self, 'user_id', n.get_uuid_value()),
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
        writer.write_enum_value("action", self.action)
        writer.write_datetime_value("at", self.at)
        writer.write_uuid_value("entityId", self.entity_id)
        writer.write_str_value("entityType", self.entity_type)
        writer.write_uuid_value("id", self.id)
        writer.write_collection_of_primitive_values("properties", self.properties)
        writer.write_str_value("traceId", self.trace_id)
        writer.write_uuid_value("userId", self.user_id)
        writer.write_additional_data_value(self.additional_data)
    

