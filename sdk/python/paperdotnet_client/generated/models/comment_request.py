from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class CommentRequest(AdditionalDataHolder, Parsable):
    """
    Create body: `{ "text", "parentId"?, "mentions"?: [userId] }`. Mentioned users are notified.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The mentions property
    mentions: Optional[list[UUID]] = None
    # The parentId property
    parent_id: Optional[UUID] = None
    # The text property
    text: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> CommentRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: CommentRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return CommentRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "mentions": lambda n : setattr(self, 'mentions', n.get_collection_of_primitive_values(UUID)),
            "parentId": lambda n : setattr(self, 'parent_id', n.get_uuid_value()),
            "text": lambda n : setattr(self, 'text', n.get_str_value()),
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
        writer.write_collection_of_primitive_values("mentions", self.mentions)
        writer.write_uuid_value("parentId", self.parent_id)
        writer.write_str_value("text", self.text)
        writer.write_additional_data_value(self.additional_data)
    

