from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class ActivityResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The actorId property
    actor_id: Optional[UUID] = None
    # The at property
    at: Optional[datetime.datetime] = None
    # The changedFields property
    changed_fields: Optional[list[str]] = None
    # The id property
    id: Optional[UUID] = None
    # The kind property
    kind: Optional[str] = None
    # The summary property
    summary: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ActivityResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ActivityResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ActivityResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "actorId": lambda n : setattr(self, 'actor_id', n.get_uuid_value()),
            "at": lambda n : setattr(self, 'at', n.get_datetime_value()),
            "changedFields": lambda n : setattr(self, 'changed_fields', n.get_collection_of_primitive_values(str)),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "kind": lambda n : setattr(self, 'kind', n.get_str_value()),
            "summary": lambda n : setattr(self, 'summary', n.get_str_value()),
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
        writer.write_uuid_value("actorId", self.actor_id)
        writer.write_datetime_value("at", self.at)
        writer.write_collection_of_primitive_values("changedFields", self.changed_fields)
        writer.write_uuid_value("id", self.id)
        writer.write_str_value("kind", self.kind)
        writer.write_str_value("summary", self.summary)
        writer.write_additional_data_value(self.additional_data)
    

